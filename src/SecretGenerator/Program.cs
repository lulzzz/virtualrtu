using System;

namespace SecretGenerator
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("----- Symmetric Key Secret Generator -----");
            Console.WriteLine("Generates a random 256 bit symmetric key base64 encoded");
            Console.WriteLine("press any key to generate a key...");
            Console.ReadKey();

            Random ran = new Random();
            byte[] buffer = new byte[32];
            ran.NextBytes(buffer);
            string base64String = Convert.ToBase64String(buffer);
            Console.WriteLine();
            Console.WriteLine("----- New Key -----");
            Console.WriteLine(base64String);
            Console.WriteLine();
            Console.WriteLine("press any key to exit...");
            Console.ReadKey();
            
        }
    }
}
