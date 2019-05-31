using IoTEdge.VirtualRtu.Configuration;
using SkunkLab.Channels;
using System;
using System.Net;
using System.Threading;

namespace EchoScadaClient
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("----Test SCADA Echo Client-----");
            Console.WriteLine("press any key to continue");
            Console.ReadKey();

            //Console.WriteLine("Enter VRTU IP address or hostname ? ");

            //string publicIP = "40.121.83.251";
            //string publicIP = "172.18.144.1";
            string publicIP = GetIPAddressString(System.Net.Dns.GetHostName());
            Random ran = new Random();
            byte[] buffer = new byte[100];
            ran.NextBytes(buffer);
            MbapHeader header = new MbapHeader()
            {
                UnitId = 1,
                ProtocolId = 1,
                TransactionId = 1,
                Length = 100
            };

            byte[] array = header.Encode();
            byte[] output = new byte[buffer.Length + array.Length];
            Buffer.BlockCopy(array, 0, output, 0, array.Length);
            Buffer.BlockCopy(buffer, 0, output, array.Length, buffer.Length);
            CancellationTokenSource cts = new CancellationTokenSource();
            IPEndPoint endpoint = new IPEndPoint(IPAddress.Parse(publicIP), 502);
            IChannel channel = ChannelFactory.Create(false, endpoint, 1024, 4048, cts.Token);
            channel.OnError += Channel_OnError;
            channel.OnClose += Channel_OnClose;
            channel.OnOpen += Channel_OnOpen;
            channel.OpenAsync().Wait();
            channel.SendAsync(output).GetAwaiter();

            Console.WriteLine("Message sent");
            Console.ReadKey();

        }

        private static string GetIPAddressString(string containerName)
        {
            IPHostEntry entry = Dns.GetHostEntry(containerName);


            string ipAddressString = null;

            foreach (var address in entry.AddressList)
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    if (address.ToString().Contains("172"))
                    {
                        ipAddressString = address.ToString();
                        break;
                    }

                }
            }

            return ipAddressString;


        }


        private static void Channel_OnOpen(object sender, ChannelOpenEventArgs e)
        {
            Console.WriteLine("Channel is open");
        }

        private static void Channel_OnClose(object sender, ChannelCloseEventArgs e)
        {
            Console.WriteLine("Channel is closed");
        }

        private static void Channel_OnError(object sender, ChannelErrorEventArgs e)
        {
            Console.WriteLine($"Channel error - {e.Error.Message}");
        }
    }
}
