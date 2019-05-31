
using Capl.Authorization;
using Capl.Authorization.Matching;
using Capl.Authorization.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace IoTEdge.FieldGateway.Function
{
    public static class VirtualRtuProvision
    {       
        private static ServiceConfig config;

        [FunctionName("VirtualRtuProvision")]
        public static IActionResult Run([HttpTrigger(AuthorizationLevel.Function, "get", Route = null)]HttpRequest req, ILogger log, ExecutionContext context)
        {
            config = new ServiceConfig();

            if(File.Exists(String.Format($"{context.FunctionAppDirectory}/secrets.json")))
            {
                //secrets.json exists use it and environment variables
                var builder = new ConfigurationBuilder()
                    .SetBasePath(context.FunctionAppDirectory)
                    .AddJsonFile("secrets.json", optional: true, reloadOnChange: true)
                    .AddEnvironmentVariables("FUNC_");

                IConfigurationRoot root = builder.Build();
                ConfigurationBinder.Bind(root, config);

            }
            else if (File.Exists(String.Format("{0}/{1}", context.FunctionAppDirectory, "local.settings.json")))
            {
                //use for local testing...do not use in production
                //remember to add the storage connection string
                var builder = new ConfigurationBuilder()
                    .SetBasePath(context.FunctionAppDirectory)
                    .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                    .AddEnvironmentVariables("FUNC_");

                IConfigurationRoot root = builder.Build();
                ConfigurationBinder.Bind(root, config);

                config.StorageConnectionString = root.GetConnectionString("StorageConnectionString");
            }
            else
            {
                //no secrets or local.settings.json files...use only environment variables 
                var builder = new ConfigurationBuilder()
                    .AddEnvironmentVariables("FUNC_");

                
                IConfigurationRoot root = builder.Build();
                ConfigurationBinder.Bind(root, config);
            }

            string luss = req.Query["luss"];

            try
            {
                EdgeGatewayConfiguration edgeConfig = ProvisionAsync(luss).GetAwaiter().GetResult();
                return (ActionResult)new OkObjectResult(edgeConfig);
            }
            catch(Exception ex)
            {
                return new BadRequestObjectResult(ex.Message);
            }

        }

        private static async Task<EdgeGatewayConfiguration> ProvisionAsync(string luss)
        {
            LussEntity entity = await LussEntity.LoadAsync(luss, config.LussStorageTableName, config.StorageConnectionString);
            if (entity == null || entity.Success.HasValue || entity.Expires < DateTime.Now)
            {
                return null;
            }


            //get the security token to call Piraeus mgmt api
            IEnumerable<Claim> claims = GetClaims(entity);
            string accessToken = GetPiraeusAccessToken();
            

            //create the CAPL policies
            string publishPolicyId = null;
            string subscribePolicyId = null;
            AuthorizationPolicy pubPolicy = CreateCaplPolicy(entity, true, out publishPolicyId);
            AuthorizationPolicy subPolicy = CreateCaplPolicy(entity, false, out subscribePolicyId);

            //add the CAPL policies to Piraeus
            AddCaplPolicy(pubPolicy, accessToken);
            AddCaplPolicy(subPolicy, accessToken);


            //create the pi-system metadata 
            string inputUriString = GetEventUriString(entity, true);
            string outputUriString = GetEventUriString(entity, false);
            EventMetadata inputMetadata = GetEventMetadata(inputUriString, publishPolicyId, subscribePolicyId, (ushort)entity.UnitId, true);
            EventMetadata outputMetadata = GetEventMetadata(outputUriString, subscribePolicyId, publishPolicyId, (ushort)entity.UnitId, false);
            
            //add the pi-systems to Piraeus
            AddEventMetadata(inputMetadata, accessToken);
            AddEventMetadata(outputMetadata, accessToken);

            //update the RTU Map
            RtuMap map = RtuMap.LoadFromConnectionStringAsync(config.RtuMapStorageContainerName, config.RtuMapFilename, config.StorageConnectionString).GetAwaiter().GetResult();
            if (map == null)
            {
                map = new RtuMap();
            }
            else
            {
                if (map.HasItem((ushort)entity.UnitId))
                {
                    map.Remove((ushort)entity.UnitId);
                }
            }

            map.Add((ushort)entity.UnitId, inputUriString, outputUriString);
            map.UpdateMapAsync(config.RtuMapStorageContainerName, config.RtuMapFilename, config.StorageConnectionString).GetAwaiter();

            //update the LUSS entity
            entity.Access = DateTime.UtcNow;
            if (!entity.Success.HasValue)
            {
                entity.Success = true;
            }

            await entity.UpdateAsync();

            string edgeSecurityToken = GetEdgeSecurityToken(entity);

            //create the cofiguration to return
            EdgeGatewayConfiguration edgeConfig = new EdgeGatewayConfiguration()
            {
                Hostname = entity.Hostname,
                ModBusContainer = entity.ModbusContainer,
                ModBusPort = entity.ModbusPort,
                ModBusPath = entity.ModbusPath,
                RtuInputPiSystem = inputUriString,
                RtuOutputPiSsytem = outputUriString,
                SecurityToken = edgeSecurityToken,
                UnitId = entity.UnitId
            };

            return edgeConfig;


        }


        private static string GetEdgeSecurityToken(LussEntity entity)
        {
            List<Claim> claims = new List<Claim>();
            claims.Add(new Claim(config.NameClaimType, entity.DeviceId));
            claims.Add(new Claim(config.RoleClaimType, entity.VirtualRtuId));

            JsonWebToken jwt = new JsonWebToken(config.SymmetricKey, claims, Convert.ToDouble(config.LifetimeMinutes), config.Issuer, config.Audience);
            return jwt.ToString();
        }

        private static string GetPiraeusAccessToken()
        {
            //string url = String.Format("https://{0}/api/manage?code={1}", config.PiraeusHostname, config.PiraeusApiToken);
            //RestRequestBuilder builder = new RestRequestBuilder("GET", url, RestConstants.ContentType.Json, true, null);
            //RestRequest request = new RestRequest(builder);
            //return request.Get<string>();

            //string url = String.Format($"https://{config.PiraeusHostname}/api/manage?code={config.PiraeusApiToken}");
            //RestRequestBuilder builder = new RestRequestBuilder("GET", url, RestConstants.ContentType.Json, true);
            //RestRequest request = new RestRequest(builder);

            //return request.Get<string>();

            HttpClient client = new HttpClient();
            
            HttpResponseMessage response = client.GetAsync(String.Format($"https://{config.PiraeusHostname}/api/manage?code={config.PiraeusApiToken}")).GetAwaiter().GetResult();
            if(response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                byte[] result = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                return JsonConvert.DeserializeObject<string>(Encoding.UTF8.GetString(result));
            }
            else
            {
                return null;
            }

        }


        private static IEnumerable<Claim> GetClaims(LussEntity entity)
        {
            List<Claim> claimset = new List<Claim>();
            claimset.Add(new Claim(config.NameClaimType, entity.DeviceId));
            claimset.Add(new Claim(config.RoleClaimType, entity.VirtualRtuId));
            return claimset;
        }

        private static AuthorizationPolicy CreateCaplPolicy(LussEntity entity, bool publishPolicy, out string policyIdUriString)
        {
            string policyId = publishPolicy ?
                            String.Format("http://www.skunklab.io/policy/{0}/unitid{1}-in", entity.VirtualRtuId, entity.UnitId) :
                            String.Format("http://www.skunklab.io/policy/{0}/unitid{1}-out", entity.VirtualRtuId, entity.UnitId);

            string claimType = publishPolicy ? config.RoleClaimType : config.NameClaimType;
            string claimValue = publishPolicy ? "vrtu" : String.Format("fieldgateway{0}", entity.UnitId);

            policyIdUriString = policyId;

            return GetPolicy(policyId, claimType, claimValue);
        }

        private static AuthorizationPolicy GetPolicy(string policyIdUriString, string matchClaimType, string matchClaimValue)
        {
            Match match = new Match(LiteralMatchExpression.MatchUri, matchClaimType, true);
            EvaluationOperation equalOperation = new EvaluationOperation(EqualOperation.OperationUri, matchClaimValue);
            Rule rule = new Rule(match, equalOperation, true);
            return new AuthorizationPolicy(rule, new Uri(policyIdUriString));
        }

        private static void AddCaplPolicy(AuthorizationPolicy policy, string securityToken)
        {

            string url = String.Format("https://{0}/api/accesscontrol/upsertaccesscontrolpolicy", config.PiraeusHostname);
            RestRequestBuilder builder = new RestRequestBuilder("PUT", url, RestConstants.ContentType.Xml, false, securityToken);
            RestRequest request = new RestRequest(builder);

            request.Put<AuthorizationPolicy>(policy);
        }

        private static void AddEventMetadata(EventMetadata metadata, string securityToken)
        {
            string url = String.Format($"https://{config.PiraeusHostname}/api/resource/UpsertPiSystemMetadata");
            RestRequestBuilder builder = new RestRequestBuilder("PUT", url, RestConstants.ContentType.Json, false, securityToken);
            RestRequest request = new RestRequest(builder);
            request.Put<EventMetadata>(metadata);
        }

        private static string GetEventUriString(LussEntity entity, bool inbound)
        {
            return inbound ? String.Format($"http://{config.PiraeusHostname}/{entity.VirtualRtuId}/unitid{entity.UnitId}-in") :
                                                    String.Format($"http://{config.PiraeusHostname}/{entity.VirtualRtuId}/unitid{entity.UnitId}-out");
        }

        private static EventMetadata GetEventMetadata(string resourceUriString, string publishPolicyIdUriString, string subscribePolicyIdUriString, ushort unitId, bool inboundRtu)
        {
            return new EventMetadata()
            {
                Audit = true,
                Description = inboundRtu ? String.Format($"RTU {unitId} input resource.") : String.Format($"RTU {unitId} output resource."),
                Enabled = true,
                RequireEncryptedChannel = true,
                ResourceUriString = resourceUriString,
                PublishPolicyUriString = publishPolicyIdUriString,
                SubscribePolicyUriString = subscribePolicyIdUriString
            };
        }



    }
}
