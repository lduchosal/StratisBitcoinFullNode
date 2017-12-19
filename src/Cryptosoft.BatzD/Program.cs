using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Protocol;
using Stratis.Bitcoin.Builder;
using Stratis.Bitcoin.Configuration;
using Stratis.Bitcoin.Features.Api;
using Stratis.Bitcoin.Features.BlockStore;
using Stratis.Bitcoin.Features.Consensus;
using Stratis.Bitcoin.Features.MemoryPool;
using Stratis.Bitcoin.Features.Miner;
using Stratis.Bitcoin.Features.RPC;
using Stratis.Bitcoin.Features.Wallet;
using Stratis.Bitcoin.Utilities;

namespace Cryptosoft.BatzD
{
    public class Program
    {
        public static void Main1(string[] args)
        {
            MainAsync(args).Wait();
        }

        static object sync = new object();
        static uint minnonce = 0;
        static uint256 minhash = uint256.Parse("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
        public static void Main(string[] argv)
        {
            var genesis = Network.BatzMain.GetGenesis();
            var queue = new System.Collections.Concurrent.ConcurrentQueue<uint>();
            var blocking = new System.Collections.Concurrent.BlockingCollection<uint>(queue, 100);


            int cpu = 6;
            for(int i=0; i<=cpu; i++)
            {

                Task.Factory.StartNew(() => {
                    foreach (var nonce in blocking.GetConsumingEnumerable())
                    {
                        var block = genesis.Clone();
                        block.Header.Nonce = nonce;
                        var hash = block.GetHash();
                        if (hash < minhash)
                        {
                            lock (sync)
                            {
                                if (hash < minhash)
                                {
                                    Console.WriteLine("Better hash found nonce : {0}, hash : {1}", nonce, hash);
                                    minhash = hash;
                                    minnonce = nonce;
                                }
                            }
                        }
                    }
                 });
            }

            for (uint nonce = 0; nonce <= uint.MaxValue; nonce++)
            {
                blocking.Add(nonce);
            }

        }

        public static async Task MainAsync(string[] args)
        {
            try
            {
                Network network = args.Contains("-testnet") ? Network.BatzTest : Network.BatzMain;
                NodeSettings nodeSettings = new NodeSettings("batz", network, ProtocolVersion.ALT_PROTOCOL_VERSION).LoadArguments(args);

                // NOTES: running BTC and STRAT side by side is not possible yet as the flags for serialization are static

                var node = new FullNodeBuilder()
                    .UseNodeSettings(nodeSettings)
                    .UseStratisConsensus()
                    .UseBlockStore()
                    .UseMempool()
                    .UseWallet()
                    .AddPowPosMining()
                    .UseApi()
                    .AddRPC()
                    .Build();

                await node.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine("There was a problem initializing the node. Details: '{0}'", ex.Message);
            }
        }
    }
}
