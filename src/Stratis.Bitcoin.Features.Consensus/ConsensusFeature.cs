﻿using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Stratis.Bitcoin.Base;
using Stratis.Bitcoin.Base.Deployments;
using Stratis.Bitcoin.BlockPulling;
using Stratis.Bitcoin.Builder;
using Stratis.Bitcoin.Builder.Feature;
using Stratis.Bitcoin.Configuration;
using Stratis.Bitcoin.Configuration.Logging;
using Stratis.Bitcoin.Connection;
using Stratis.Bitcoin.Features.Consensus.CoinViews;
using Stratis.Bitcoin.Features.Consensus.Rules;
using Stratis.Bitcoin.Features.Consensus.Rules.CommonRules;
using Stratis.Bitcoin.Interfaces;
using Stratis.Bitcoin.P2P.Protocol.Payloads;
using Stratis.Bitcoin.Utilities;

[assembly: InternalsVisibleTo("Stratis.Bitcoin.Features.Consensus.Tests")]

namespace Stratis.Bitcoin.Features.Consensus
{
    public class ConsensusFeature : FullNodeFeature, INodeStats
    {
        private readonly DBreezeCoinView dBreezeCoinView;

        private readonly Network network;

        private readonly ConcurrentChain chain;

        private readonly PowConsensusValidator consensusValidator;

        private readonly LookaheadBlockPuller blockPuller;

        private readonly CoinView coinView;

        private readonly ChainState chainState;

        private readonly IConnectionManager connectionManager;

        /// <summary>Global application life cycle control - triggers when application shuts down.</summary>
        private readonly INodeLifetime nodeLifetime;

        private readonly Signals.Signals signals;

        /// <summary>Manager of the longest fully validated chain of blocks.</summary>
        private readonly ConsensusLoop consensusLoop;

        private readonly NodeSettings nodeSettings;

        private readonly NodeDeployments nodeDeployments;

        private readonly StakeChainStore stakeChain;

        /// <summary>Consensus settings from configuration.</summary>
        private readonly ConsensusSettings consensusSettings;

        private readonly IRuleRegistration ruleRegistration;
        private readonly IConsensusRules consensusRules;

        /// <summary>Instance logger.</summary>
        private readonly ILogger logger;

        /// <summary>Logger factory to create loggers.</summary>
        private readonly ILoggerFactory loggerFactory;

        /// <summary>Provider of time functions.</summary>
        private readonly IDateTimeProvider dateTimeProvider;

        private readonly ConsensusManager consensusManager;

        /// <summary>Consensus statistics logger.</summary>
        private readonly ConsensusStats consensusStats;

        public ConsensusFeature(
            IAsyncLoopFactory asyncLoopFactory,
            DBreezeCoinView dBreezeCoinView,
            Network network,
            PowConsensusValidator consensusValidator,
            ConcurrentChain chain,
            LookaheadBlockPuller blockPuller,
            CoinView coinView,
            ChainState chainState,
            IConnectionManager connectionManager,
            INodeLifetime nodeLifetime,
            Signals.Signals signals,
            ConsensusLoop consensusLoop,
            NodeSettings nodeSettings,
            NodeDeployments nodeDeployments,
            ILoggerFactory loggerFactory,
            IDateTimeProvider dateTimeProvider,
            ConsensusManager consensusManager,
            ConsensusStats consensusStats,
            ConsensusSettings consensusSettings,
            IRuleRegistration ruleRegistration,
            IConsensusRules consensusRules,
            StakeChainStore stakeChain = null)
        {
            this.dBreezeCoinView = dBreezeCoinView;
            this.consensusValidator = consensusValidator;
            this.chain = chain;
            this.blockPuller = blockPuller;
            this.coinView = coinView;
            this.chainState = chainState;
            this.connectionManager = connectionManager;
            this.nodeLifetime = nodeLifetime;
            this.signals = signals;
            this.network = network;
            this.consensusLoop = consensusLoop;
            this.nodeSettings = nodeSettings;
            this.nodeDeployments = nodeDeployments;
            this.stakeChain = stakeChain;
            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);
            this.loggerFactory = loggerFactory;
            this.dateTimeProvider = dateTimeProvider;
            this.consensusManager = consensusManager;
            this.consensusStats = consensusStats;
            this.consensusSettings = consensusSettings;
            this.ruleRegistration = ruleRegistration;
            this.consensusRules = consensusRules;

            this.chainState.MaxReorgLength = this.network.Consensus.Option<PowConsensusOptions>().MaxReorgLength;
        }

        /// <inheritdoc />
        public void AddNodeStats(StringBuilder benchLogs)
        {
            if (this.chainState?.ConsensusTip != null)
            {
                benchLogs.AppendLine("Consensus.Height: ".PadRight(LoggingConfiguration.ColumnLength + 3) +
                                     this.chainState.ConsensusTip.Height.ToString().PadRight(8) +
                                     " Consensus.Hash: ".PadRight(LoggingConfiguration.ColumnLength + 3) +
                                     this.chainState.ConsensusTip.HashBlock);
            }
        }

        /// <inheritdoc />
        public override void Initialize()
        {
            this.dBreezeCoinView.InitializeAsync().GetAwaiter().GetResult();
            this.consensusLoop.StartAsync().GetAwaiter().GetResult();

            this.chainState.ConsensusTip = this.consensusLoop.Tip;
            this.connectionManager.Parameters.TemplateBehaviors.Add(new BlockPullerBehavior(this.blockPuller, this.loggerFactory));

            var flags = this.nodeDeployments.GetFlags(this.consensusLoop.Tip);
            if (flags.ScriptFlags.HasFlag(ScriptVerify.Witness))
                this.connectionManager.AddDiscoveredNodesRequirement(NetworkPeerServices.NODE_WITNESS);

            this.stakeChain?.LoadAsync().GetAwaiter().GetResult();

            this.signals.SubscribeForBlocks(this.consensusStats);

            this.consensusRules.Register(this.ruleRegistration);
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            // First, we need to wait for the consensus loop to finish.
            // Only then we can flush our coinview safely.
            // Otherwise there is a race condition and a new block
            // may come from the consensus at wrong time.
            this.consensusLoop.Stop();

            var cache = this.coinView as CachedCoinView;
            if (cache != null)
            {
                this.logger.LogInformation("Flushing Cache CoinView...");
                cache.FlushAsync().GetAwaiter().GetResult();
            }

            this.dBreezeCoinView.Dispose();
        }
    }

    /// <summary>
    /// A class providing extension methods for <see cref="IFullNodeBuilder"/>.
    /// </summary>
    public static class FullNodeBuilderConsensusExtension
    {
        public static IFullNodeBuilder UseConsensus(this IFullNodeBuilder fullNodeBuilder)
        {
            LoggingConfiguration.RegisterFeatureNamespace<ConsensusFeature>("consensus");
            LoggingConfiguration.RegisterFeatureClass<ConsensusStats>("bench");

            fullNodeBuilder.ConfigureFeature(features =>
            {
                features
                .AddFeature<ConsensusFeature>()
                .FeatureServices(services =>
                {
                    // TODO: this should be set on the network build
                    fullNodeBuilder.Network.Consensus.Options = new PowConsensusOptions();

                    services.AddSingleton<ICheckpoints, Checkpoints>();
                    services.AddSingleton<NBitcoin.Consensus.ConsensusOptions, PowConsensusOptions>();
                    services.AddSingleton<PowConsensusValidator>();
                    services.AddSingleton<DBreezeCoinView>();
                    services.AddSingleton<CoinView, CachedCoinView>();
                    services.AddSingleton<LookaheadBlockPuller>();
                    services.AddSingleton<ConsensusLoop>();
                    services.AddSingleton<ConsensusManager>().AddSingleton<IBlockDownloadState, ConsensusManager>().AddSingleton<INetworkDifficulty, ConsensusManager>();
                    services.AddSingleton<IGetUnspentTransaction, ConsensusManager>();
                    services.AddSingleton<ConsensusController>();
                    services.AddSingleton<ConsensusStats>();
                    services.AddSingleton<ConsensusSettings>();
                    services.AddSingleton<IConsensusRules, ConsensusRules>();
                    services.AddSingleton<IRuleRegistration, CoreConsensusRules>();
                });
            });

            return fullNodeBuilder;
        }

        public static IFullNodeBuilder UseStratisConsensus(this IFullNodeBuilder fullNodeBuilder)
        {
            LoggingConfiguration.RegisterFeatureNamespace<ConsensusFeature>("consensus");
            LoggingConfiguration.RegisterFeatureClass<ConsensusStats>("bench");

            fullNodeBuilder.ConfigureFeature(features =>
            {
                features
                    .AddFeature<ConsensusFeature>()
                    .FeatureServices(services =>
                    {
                        fullNodeBuilder.Network.Consensus.Options = new PosConsensusOptions();

                        if (fullNodeBuilder.NodeSettings.Testnet)
                        {
                            fullNodeBuilder.Network.Consensus.Option<PosConsensusOptions>().CoinbaseMaturity = 10;
                            fullNodeBuilder.Network.Consensus.Option<PosConsensusOptions>().StakeMinConfirmations = 10;
                        }

                        services.AddSingleton<ICheckpoints, Checkpoints>();
                        services.AddSingleton<PowConsensusValidator, PosConsensusValidator>();
                        services.AddSingleton<DBreezeCoinView>();
                        services.AddSingleton<CoinView, CachedCoinView>();
                        services.AddSingleton<LookaheadBlockPuller>();
                        services.AddSingleton<ConsensusLoop>();
                        services.AddSingleton<StakeChainStore>().AddSingleton<StakeChain, StakeChainStore>(provider => provider.GetService<StakeChainStore>());
                        services.AddSingleton<StakeValidator>();
                        services.AddSingleton<ConsensusManager>().AddSingleton<IBlockDownloadState, ConsensusManager>().AddSingleton<INetworkDifficulty, ConsensusManager>();
                        services.AddSingleton<ConsensusController>();
                        services.AddSingleton<ConsensusStats>();
                        services.AddSingleton<ConsensusSettings>();
                        services.AddSingleton<IConsensusRules, ConsensusRules>();
                        services.AddSingleton<IRuleRegistration, CoreConsensusRules>();
                    });
            });

            return fullNodeBuilder;
        }

        public class CoreConsensusRules : IRuleRegistration
        {
            public IEnumerable<ConsensusRule> GetRules()
            {
                yield return new BlockPreviousHeaderRule();

                // rules that are inside the method ContextualCheckBlockHeader
                yield return new CheckpointsRule();
                yield return new AssumeValidRule();

                // rules that are inside the method ContextualCheckBlock
                yield return new Bip113ActivationRule();
                yield return new Bip34ActivationRule();
                yield return new WitnessCommitmentsRule();
                yield return new BlockSizeRule();

                // rules that are inside the method CheckBlock
                yield return new BlockMerkleRootRule();
                yield return new EnsureCoinbaseRule();
                yield return new CheckTransactionRule();
                yield return new CheckSigOpsRule();
            }
        }
    }
}
