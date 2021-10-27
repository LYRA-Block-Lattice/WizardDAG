﻿using Lyra.Core.Accounts;
using Lyra.Core.Blocks;
using Lyra.Data.API;
using Lyra.Data.Blocks;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Lyra.Core.API
{
    public interface INodeAPI
    {
        #region Blocklist information methods
        Task<GetVersionAPIResult> GetVersionAsync(int apiVersion, string appName, string appVersion);

        Task<BlockAPIResult> GetServiceGenesisBlockAsync();
        Task<BlockAPIResult> GetLyraTokenGenesisBlockAsync();

        Task<GetSyncStateAPIResult> GetSyncStateAsync();

        // this one can be cached for a few milliseconds
        Task<AccountHeightAPIResult> GetSyncHeightAsync();

        Task<GetListStringAPIResult> GetTokenNamesAsync(string AccountId, string Signature, string keyword);

        // this one can be cached for a few seconds
        Task<BlockAPIResult> GetLastServiceBlockAsync();

        // this one can be definitely cached forever as the result never changes if the block exists
        Task<BlockAPIResult> GetTokenGenesisBlockAsync(string AccountId, string TokenTicker, string Signature);

        Task<BlockAPIResult> GetLastConsolidationBlockAsync();
        Task<MultiBlockAPIResult> GetConsolidationBlocksAsync(string AccountId, string Signature, long startHeight, int count);
        Task<MultiBlockAPIResult> GetBlocksByConsolidationAsync(string AccountId, string Signature, string consolidationHash);
        //Task<GetListStringAPIResult> GetUnConsolidatedBlocks(string AccountId, string Signature);
        // add new api, all upgraded, delete old api, done.
        Task<TransactionsAPIResult> SearchTransactionsAsync(string accountId, long startTimeTicks, long endTimeTicks, int count);
        Task<MultiBlockAPIResult> GetBlocksByTimeRangeAsync(DateTime startTime, DateTime endTime);
        Task<GetListStringAPIResult> GetBlockHashesByTimeRangeAsync(DateTime startTime, DateTime endTime);
        Task<MultiBlockAPIResult> GetBlocksByTimeRangeAsync(long startTimeTicks, long endTimeTicks);
        Task<GetListStringAPIResult> GetBlockHashesByTimeRangeAsync(long startTimeTicks, long endTimeTicks);
        #endregion Blocklist information methods

        #region Account maintenance methods

        // TO DO add authentication for Account maintenance methods
        // using Diffie-Helman shared secret algorithm with AccountId as a sender's public key and Node's account id as the recipient's public key.
        // This way only account holders can request the account information which will prevent DoS and add some privacy in centralized network configuration. 

        Task<AccountHeightAPIResult> GetAccountHeightAsync(string AccountId);
        Task<BlockAPIResult> GetLastBlockAsync(string AccountId);

        Task<BlockAPIResult> GetBlockByIndexAsync(string AccountId, long Index);
        Task<BlockAPIResult> GetServiceBlockByIndexAsync(string blockType, long Index);

        // Retrives a block by its hash
        Task<BlockAPIResult> GetBlockByHashAsync(string AccountId, string Hash, string Signature);
        Task<BlockAPIResult> GetBlockAsync(string Hash);
        Task<BlockAPIResult> GetBlockBySourceHashAsync(string sourceHash);
        Task<MultiBlockAPIResult> GetBlocksByRelatedTxAsync(string hash);

        Task<NewTransferAPIResult> LookForNewTransferAsync(string AccountId, string Signature);
        Task<NewTransferAPIResult2> LookForNewTransfer2Async(string AccountId, string Signature);
        Task<NewFeesAPIResult> LookForNewFeesAsync(string AccountId, string Signature);

        Task<NonFungibleListAPIResult> GetNonFungibleTokensAsync(string AccountId, string Signature);
        #endregion Account maintenance methods

        #region voting
        List<Voter> GetVoters(VoteQueryModel model);
        List<Vote> FindVotes(VoteQueryModel model);
        FeeStats GetFeeStats();
        #endregion

        #region Reward trade methods

        Task<ActiveTradeOrdersAPIResult> GetActiveTradeOrdersAsync(string AccountId, string SellToken, string BuyToken, TradeOrderListTypes OrderType, string Signature);

        Task<TradeAPIResult> LookForNewTradeAsync(string AccountId, string BuyTokenCode, string SellTokenCode, string Signature);

        #endregion

        #region Liquidate Pool
        Task<PoolInfoAPIResult> GetPoolAsync(string token0, string token1);
        #endregion

        Task<MultiBlockAPIResult> GetAllBrokerAccountsForOwnerAsync(string ownerAccount);

    }

    public interface INodeTransactionAPI
    {
        Task<BillBoard> GetBillBoardAsync();
        Task<List<TransStats>> GetTransStatsAsync();
        Task<string> GetDbStatsAsync();

        #region Authorization methods 
        // These methods return authorization result and authorizers' signatures if approved

        Task<AuthorizationAPIResult> SendTransferAsync(SendTransferBlock block);

        Task<AuthorizationAPIResult> ReceiveTransferAsync(ReceiveTransferBlock block);
        Task<AuthorizationAPIResult> ReceiveFeeAsync(ReceiveNodeProfitBlock block);

        Task<AuthorizationAPIResult> ReceiveTransferAndOpenAccountAsync(OpenWithReceiveTransferBlock block);

        Task<AuthorizationAPIResult> ImportAccountAsync(ImportAccountBlock block);

        Task<AuthorizationAPIResult> OpenAccountWithGenesisAsync(LyraTokenGenesisBlock block);

        Task<AuthorizationAPIResult> OpenAccountWithImportAsync(OpenAccountWithImportBlock block);

        Task<AuthorizationAPIResult> CreateTokenAsync(TokenGenesisBlock block);

        #endregion Authorization methods

        #region Reward Trade Athorization Methods
        
        Task<TradeOrderAuthorizationAPIResult> TradeOrderAsync(TradeOrderBlock block);

        Task<AuthorizationAPIResult> TradeAsync(TradeBlock block);

        Task<AuthorizationAPIResult> ExecuteTradeOrderAsync(ExecuteTradeOrderBlock block);

        Task<AuthorizationAPIResult> CancelTradeOrderAsync(CancelTradeOrderBlock block);
        
        #endregion
    }
}
