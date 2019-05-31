function New-VrtuDeploy()
{
    param ([string]$DnsName, [string]$Location, [string]$PoolSize, [string]$Issuer, [string]$Audience, [string]$LifetimeMinutes,  [string]$ClaimTypes, [string]$ClaimValues, [string]$PiraeusHostname, [string]$RtuMapSasUri, [string]$ResourceGroupName, [string]$SymmetricKey = "//////////////////////////////////////////8=", [string]$ClusterName = "vrtucluster", [int]$NodeCount = 1, [string]$VMSize = "Standard_D2s_v3")																												 

            #Remove previous deployments from kubectl
            $cleanup = Read-Host "Clean up previous deployment (Y/N) ? "
            if($cleanup.ToLowerInvariant() -eq "y")
            {
				
                $clusterName = Read-Host "Enter previous cluster name [Enter blank == vrtucluster] "
                $resourceGroup = Read-Host "Enter previous resource group name [Enter blank == myResourceGroup] "
                
                if($clusterName.Length -eq 0)
                {
					$clusterName = "vrtucluster"
                }
                
                if($resourceGroup.Length -eq 0)
                {
					$resourceGroup = "myResourceGroup"
                }
                
                $condition1 = "users.clusterUser_" + $resourceGroup + "_" + $clusterName
                $condition2 = "clusters." + $clusterName
                kubectl config unset $condition1
                kubectl config unset $condition2
            }

            $step = 1
	    $createRG = Read-Host "Create Resource Group (Y/N) ? "
	    if($createRG.ToLowerInvariant() -eq "y")
            {

              	#create the resource group for the deployment
              	Write-Host "Step $step - Create resource group '$ResourceGroupName'" -ForegroundColor Green
            	az group create --name $ResourceGroupName --location $Location
            }
            else
	    {
		#not creating the Resouce Group
            }
            $step++

            #create service principal
            Write-Host "-- Step $step - Create service principal" -ForegroundColor Green
            $creds = az ad sp create-for-rbac  --skip-assignment
            $v1 = $creds[1].Replace(",","").Replace(":","=").Replace(" ","").Replace('"',"")
            $sd1 = ConvertFrom-StringData -StringData $v1
            $appId = $sd1.Values[0]
            $v2 = $creds[4].Replace(",","").Replace(":","=").Replace(" ","").Replace('"',"")
            $sd2 = ConvertFrom-StringData -StringData $v2
            $pwd = $sd2.Values[0]
            $step++
            Write-Host "Sleeping 30 seconds"
            Start-Sleep -Seconds 30

            #create AKS cluster
            Write-Host "-- Step $step - Create AKS cluster" -ForegroundColor Green
            az aks create --resource-group $ResourceGroupName --name $ClusterName --node-count $NodeCount --service-principal $appId --client-secret $pwd --node-vm-size $VMSize --enable-vmss 

            $step++

            #get AKS credentials
            Write-Host "-- Step $step - Get AKS credentials" -ForegroundColor Green
            GetAksCredentials $ResourceGroupName $ClusterName
            #az aks get-credentials --resource-group $ResourceGroupName --name $ClusterName
            $step++

            #apply RBAC
            Write-Host "-- Step $step - Apply kubectl RBAC" -ForegroundColor Green
            ApplyYaml "./helm-rbac.yaml"
            $step++

            #initialize tiller with helm
            Write-Host "-- Step $step - Intialize tiller" -ForegroundColor Green
            helm init --service-account tiller
            Write-Host "...waiting 45 seconds for Tiller to start" -ForegroundColor Yellow
            Start-Sleep -Seconds 45
            $step++

            
    

            #apply the virtual rtu helm chart
            Write-Host "-- Step $step - Deploying helm chart for vrtu" -ForegroundColor Green
	    #$poolSize = $PoolSize 
	    #$lifetimeMinutes = $LifetimeMinutes 
	    $poolSize = '\"' + $PoolSize + '\"'
	    $lifetimeMinutes = '\"'+ $LifetimeMinutes + '\"'

            helm install ./iotedge-vrtu --name virtualrtu --namespace kube-system --set poolSize=$poolSize --set claimTypes=$ClaimTypes --set claimValues=$ClaimValues --set issuer=$Issuer --set audience=$Audience --set lifetimeMinutes=$lifetimeMinutes --set symmetricKey=$SymmetricKey --set piraeusHostname=$PiraeusHostname --set rtuMapSasUri=$RtuMapSasUri 
            $step++

            kubectl expose deployment virtualrtu-iotedge-vrtu-vrtu --type=LoadBalancer --name=vrtu-service --namespace kube-system
            
            Write-Host "-- Step $step - External IP" -ForegroundColor Green
            $IP = GetExternalIP
            Write-Host "Got external IP = $IP" -ForegroundColor Yellow
            # Get the resource-id of the public ip

            $PUBLICIPID=$(az network public-ip list --query "[?ipAddress!=null]|[?contains(ipAddress, '$IP')].[id]" --output tsv)
            $step++

            update the azure network with the public IP ID
            Write-Host "-- Step $step - Update Azure Network with Public IP ID" -ForegroundColor Green
            az network public-ip update --ids $PUBLICIPID --dns-name $dnsName
            $step++

            Write-Host "--- Done :-) Dare Mighty Things ---" -ForegroundColor Cyan

}

function GetAksCredentials()
{
    param([string]$rgn, [string]$cn)

    $looper = $true
    while($looper)
    {
        try
        {         
            az aks get-credentials --resource-group $rgn --name $cn
            $looper = $false
        }
        catch
        {
            Write-Host "Waiting 30 seconds to try get aks credentials again..." -ForegroundColor Yellow
            Start-Sleep -Seconds 30
        }    
    }
}


function GetExternalIP()
{
    $looper = $TRUE
    while($looper)
    {   $externalIP = ""                  
        #$lineValue = kubectl get service -l app=vrtu --namespace kube-system
	$lineValue = kubectl get service vrtu-service  --namespace kube-system
        
        Write-Host "Last Exit Code for get external ip $LASTEXITCODE" -ForegroundColor White
        if($LASTEXITCODE -ne 0 )
        {
            Write-Host "Try get external ip...waiting 30 seconds" -ForegroundColor Yellow
            Start-Sleep -Seconds 30
        }  
        elseif($lineValue -ne $null -and $lineValue.Length -gt 0)
        {
	    $lineItems = $lineParams.Split("/r/n")
            $line = $lineItems[1]
            $items = $line.Split(" ")
            if($items.Count == 6)
	    {
              $externalIP = $items[3]   
	    }
        }
        
              
        if($externalIP -eq "<pending>")
        {        
            Write-Host "External IP is pending...waiting 30 seconds" -ForegroundColor Yellow
            Start-Sleep -Seconds 30
        }
        elseif($externalIP.Length -eq 0)
        {
            Write-Host "External IP is zero length...waiting 30 seconds" -ForegroundColor Yellow
            Start-Sleep -Seconds 30
        }
        else
        {
			$looper = $FALSE
            Write-Host "External IP is $externalIP" -ForegroundColor Magenta
            return $externalIP
        }
    }
}

#---- functions ----
function SetNodeLabel
{
    param([string]$nodeMatchValue, [string]$key, [string]$value)
    
    $looper = $true
    while($looper)
    {    
        $nodes = kubectl get nodes
        if($LASTEXITCODE -ne 0)
        {
            Write-Host "Waiting 10 seconds to get nodes from kubectl..." -ForegroundColor Yellow
            Start-Sleep -Seconds 10
        }
        else
        {
            foreach($node in $nodes)
            {
               $nodeVal = $node.Split(" ")[0]
               if($nodeVal.Contains($nodeMatchValue))
               {
		            kubectl label nodes $nodeVal "$key=$value"
                    if($LASTEXITCODE -ne 0)
                    {
                        Write-Host "Set node label failed. Waiting 10 seconds to try again..." -ForegroundColor Yellow
                        Start-Sleep -Seconds 10
                    }
                    else
                    {
                        $looper = $false
                    }
               }
            }
        }
    }
}

function ApplyYaml
{
    param([string]$file)

    $looper = $true
    while($looper)
    {
        kubectl apply -f $file
        if($LASTEXITCODE -ne 0)
        {
            Write-Host "kubectl apply failed for $file. Waiting 10 seconds to try again..." -ForegroundColor Yellow
            Start-Sleep -Seconds 10
        }
        else
        {
            $looper = $false
        }
    }
}

function UpdateYaml()
{
    Param ([string]$newValue, [string]$matchString, [string]$filename)

    (Get-Content $filename) -replace $matchString,$newValue | out-file $filename -Encoding ascii
}

#---- end functions


