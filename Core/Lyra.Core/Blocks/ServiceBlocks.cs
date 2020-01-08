﻿using System.Collections.Generic;
using Newtonsoft.Json;

namespace Lyra.Core.Blocks
{
    /// <summary>
    /// ServiceBlock make trust pass down mode
    /// every new block must be certificated by previous block (sign by private key)
    /// this is a reverse blockchain
    /// every consolidate block must be certificated by previous serviceblock
    /// </summary>
    
    // Service blocks are only generated when there is a change in Authorizers list;
    // The block must be generated by the candidate entering the authorization sample;
    // the interval between the block must be at least 1 minute;
    // the block contains the full list of authorizers;
    // only one change at the time is allowed (i.e. one authorizer out and one in)
    public class ServiceBlock : TransactionBlock
    {
        //public Dictionary<string, NodeInfo> Authorizers { get; set; }
        //public List<NodeInfo> Authorizers { get; set; }

        //public List<NodeInfo> Candidates { get; set; }

        ///// <summary>
        ///// Defines whether this shard is a primary (i.e. independant, isolated) network
        ///// which has generated its own Lyra Gas token,
        ///// or otherwise it is secondary shard which is dependant on another primary shard (and accepts it's Lyra Gas token).
        ///// </summary>
        //public bool IsPrimaryShard { get; set; }

        ///// <summary>
        ///// For primary shards, the Shard Public Key is hardcoded in the software.
        ///// The very first service block must be signed by the corresponding one-time private key.
        ///// </summary>
        //public string ShardPublicKey { get; set; }

        ///// <summary>
        ///// The list of shard ids that can be accepted by this shard.
        ///// If this is a primary shard, the list can be empty.
        ///// If this is secondary shard, the list must contain at least the shard id of the corresponding primary shard.
        ///// </summary>
        //public List<string> AcceptedShards { get; set; }

        // Amount of fee for LYRA gas and custom token transfers;
        public decimal TransferFee { get; set; }

        public decimal TokenGenerationFee { get; set; }

        public decimal TradeFee { get; set; }

        //public int TransferDifficulty { get; set; }
        //public int TokenGenerationDifficulty { get; set; }
        //public int TradeDifficulty { get; set; }

        protected override string GetExtraData()
        {
            string extraData = base.GetExtraData();
            //extraData = extraData + JsonConvert.SerializeObject(Authorizers) + "|";
            //extraData = extraData + JsonConvert.SerializeObject(Candidates) + "|";
            //extraData = extraData + IsPrimaryShard + "|";
            //extraData = extraData + ShardPublicKey + "|";
            //extraData = extraData + JsonConvert.SerializeObject(AcceptedShards) + "|";
            extraData = extraData + JsonConvert.SerializeObject(TransferFee) + "|";
            extraData = extraData + JsonConvert.SerializeObject(TokenGenerationFee) + "|";
            extraData = extraData + JsonConvert.SerializeObject(TradeFee) + "|";
            return extraData;
        }

        public override BlockTypes GetBlockType()
        {
            return BlockTypes.Service;
        }

        public override TransactionInfoEx GetTransaction(TransactionBlock previousBlock)
        {
            throw new System.NotImplementedException();
        }
    }

//    public class ServiceGenesisBlock : ServiceBlock
//    {
//        /// <summary>
//        /// The signature generated using one-time shard private key 
//        /// </summary>
////        public string ShardSignature { get; set; }

//        protected override string GetExtraData()
//        {
//            string extraData = base.GetExtraData();
//            //extraData = extraData + IsPrimaryShard + "|";
//            //extraData = extraData + ShardPublicKey + "|";
//            //extraData = extraData + ShardSignature + "|";
//            return extraData;
//        }

//        public override BlockTypes GetBlockType()
//        {
//            return BlockTypes.ServiceGenesis;
//        }
//    }

    public class ConsolidationBlock : ServiceBlock
    {
        // do nothing?
        public string LastServiceBlockHash { get; set; }
        public string MerkelTreeHash { get; set; }

        public override BlockTypes GetBlockType()
        {
            return BlockTypes.Consolidation;
        }

        protected override string GetExtraData()
        {
            return base.GetExtraData() +
                $"|{LastServiceBlockHash}|{MerkelTreeHash}";
        }
    }

    // Sync block:
    // 1) allows smooth transition between authorization samples; 
    // 2) ensures that stuck transactions are cleaned up.
    // Sync block does not include any changes; the samples is taken from the latest service block.
    // Transactions that reference an old service or sync block (< height - 1) are rejected.
    // The block is generated by the authorizer with largest stake;
    // If no block is generated within 2 minutes, the authorizer with the second stake must generate it, etc.
    //public class SyncBlock : Block
    //{
    //    public string LastServiceBlockHash { get; set; }

    //    public override BlockTypes GetBlockType()
    //    {
    //        return BlockTypes.Sync;
    //    }
    //}
}