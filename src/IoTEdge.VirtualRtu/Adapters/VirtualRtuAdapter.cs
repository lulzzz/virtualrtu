using IoTEdge.VirtualRtu.Configuration;
using IoTEdge.VirtualRtu.Pooling;
using Piraeus.Clients.Mqtt;
using SkunkLab.Channels;
using SkunkLab.Protocols.Mqtt;
using System;
using System.Threading.Tasks;

namespace IoTEdge.VirtualRtu.Adapters
{
    public class VirtualRtuAdapter : IDisposable
    {
        public VirtualRtuAdapter(RtuMap map, IChannel channel)
        {
            this.Id = Guid.NewGuid().ToString();
            this.map = map;
            this.channel = channel;

            pool = ConnectionPool.Create();
            client = pool.Take();
            
        }

        

        public event System.EventHandler<AdapterErrorEventArgs> OnError;
        public event System.EventHandler<AdapterCloseEventArgs> OnClose;
        private ConnectionPool pool;
        private IChannel channel;
        private RtuMap map;
        private byte? unitId;
        private PiraeusMqttClient client;
        private string contentType = "application/octet-stream";
        private bool disposed;

        public string Id { get; internal set; }

        public async Task StartAsync()
        {
            channel.OnReceive += Channel_OnReceive;
            channel.OnError += Channel_OnError;
            channel.OnClose += Channel_OnClose;
            await channel.OpenAsync();
            StartReceive();

        }
        private void StartReceive()
        {
            Task task = channel.ReceiveAsync();
            Task.WhenAll(task);
        }
        private void Channel_OnReceive(object sender, ChannelReceivedEventArgs e)
        {
            try
            {
                MbapHeader header = MbapHeader.Decode(e.Message);

                if (!unitId.HasValue)
                {
                    unitId = header.UnitId;
                }

                if (unitId.HasValue && header.UnitId == unitId.Value)
                {
                    RtuPiSystem piSystem = map.GetItem((ushort)unitId.Value);

                    if (piSystem == null)
                    {
                        throw new Exception($"PI-System not found for unit id - {unitId.Value}");
                    }
                    else
                    {
                        client.SubscribeAsync(piSystem.RtuOutputEvent, QualityOfServiceLevelType.AtLeastOnce, ReceiveOutput).GetAwaiter();
                        client.PublishAsync(QualityOfServiceLevelType.AtLeastOnce, piSystem.RtuInputEvent, contentType, e.Message).GetAwaiter();
                    }
                }
                else
                {
                    throw new Exception("Unit Id missing from SCADA client message.");
                }

            }
            catch(Exception ex)
            {
                OnError?.Invoke(this, new AdapterErrorEventArgs(Id, ex));
            }
        }

        private void Channel_OnClose(object sender, ChannelCloseEventArgs e)
        {
            OnClose?.Invoke(this, new AdapterCloseEventArgs(Id));
        }

        private void Channel_OnError(object sender, ChannelErrorEventArgs e)
        {
            OnError?.Invoke(this, new AdapterErrorEventArgs(Id, e.Error));
        }

        private void ReceiveOutput(string topic, string contentType, byte[] message)
        {
            try
            {
                channel.SendAsync(message).GetAwaiter();
            }
            catch(Exception ex)
            {
                OnError?.Invoke(this, new AdapterErrorEventArgs(Id, ex));
            }
        }

        protected void Disposing(bool dispose)
        {
            if (dispose & !disposed)
            {
                disposed = true;

                if (client != null)
                {
                    pool.Put(client);
                    channel.Dispose();
                }
            }
        }

        public void Dispose()
        {
            Disposing(true);
            GC.SuppressFinalize(this);
        }
    }
}
