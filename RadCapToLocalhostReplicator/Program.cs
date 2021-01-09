using System;
using System.Threading;
using System.Threading.Tasks;

namespace RadCapToLocalhostReplicator
{
    internal class Program
    {
        private static async Task Main()
        {
            try
            {
                var options = await Options.InitAsync();
                if (Options.AreInvalid(options, out var message))
                {
                    Console.WriteLine(message);
                    Console.ReadKey();
                    return;
                }

                using var transmitter = new RadCapToLocalhost(options!, Console.WriteLine, null);
                Console.WriteLine("Press Enter to exit.");
                using var cts = new CancellationTokenSource();
                await await Task.WhenAny(transmitter.RunAsync(cts.Token), Task.Run(Console.In.ReadLineAsync, cts.Token));
            }
            catch (Exception a)
            {
                Console.WriteLine(a);
                Console.WriteLine($"{Environment.NewLine}Unexpected error. Application will be closed.");
                Console.ReadKey();
            }
        }
    }
}