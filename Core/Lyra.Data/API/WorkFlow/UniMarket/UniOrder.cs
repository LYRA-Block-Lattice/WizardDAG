﻿using Lyra.Core.API;
using Lyra.Core.Blocks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lyra.Data.API.WorkFlow.UniMarket
{
    public enum HoldTypes
    {
        Token,
        Fiat,
        NFT,
        SKU,
        SVC,        
    }

    public class UniOrder
    {
        public string daoId { get; set; } = null!;   // DAO account ID
        public string dealerId { get; set; } = null!;
        public TradeDirection dir { get; set; }

        public HoldTypes propType { get; set; }
        /// <summary>
        /// ticker to give
        /// </summary>
        public string offering { get; set; } = null!;

        public HoldTypes moneyType { get; set; }
        /// <summary>
        /// ticker to get
        /// </summary>        
        public string biding { get; set; } = null!;

        /// <summary>
        /// price in specified money type, fiat or token
        /// </summary>
        public decimal price { get; set; }

        /// <summary>
        /// always crypto
        /// </summary>
        public decimal amount { get; set; }
        /// <summary>
        /// always fiat
        /// </summary>
        public decimal limitMin { get; set; }
        /// <summary>
        /// always fiat
        /// </summary>
        public decimal limitMax { get; set; }

        /// <summary>
        /// buyer paying methods, online or offline, token or fiat
        /// </summary>
        public string[] payBy { get; set; } = null!;

        /// <summary>
        /// the amount of collateral, always be LYR token
        /// </summary>
        public decimal cltamt { get; set; }

        public override bool Equals(object? obOther)
        {
            if (null == obOther)
                return false;

            if (ReferenceEquals(this, obOther))
                return true;

            if (GetType() != obOther.GetType())
                return false;

            var ob = obOther as UniOrder;
            if(ob == null)
                return false;

            return daoId == ob.daoId &&
                dealerId == ob.dealerId &&
                dir == ob.dir &&
                propType == ob.propType &&
                offering == ob.offering &&
                moneyType == ob.moneyType &&
                biding == ob.biding &&
                amount == ob.amount &&
                cltamt == ob.cltamt &&
                price == ob.price &&
                limitMin == ob.limitMin &&
                limitMax == ob.limitMax &&
                payBy.SequenceEqual(ob.payBy);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(HashCode.Combine(daoId, dealerId, dir, propType, offering, moneyType, biding),
                HashCode.Combine(price, amount, cltamt, limitMin, limitMax, payBy));
        }

        public string GetExtraData(Block parent)
        {
            string extraData = "";
            extraData += daoId + "|";
            extraData += $"{dealerId}|";
            extraData += $"{dir}|";
            extraData += $"{propType}|";
            extraData += $"{offering}|";
            extraData += $"{moneyType}|";
            extraData += $"{biding}|";
            extraData += $"{price.ToBalanceLong()}|";
            extraData += $"{amount.ToBalanceLong()}|";
            extraData += $"{cltamt.ToBalanceLong()}|";

            extraData += $"{limitMin}|";
            extraData += $"{limitMax}|";
            extraData += $"{string.Join(",", payBy)}|";
            return extraData;
        }

        public override string? ToString()
        {
            string? result = base.ToString();
            result += $"DAO ID: {daoId}\n";
            result += $"Dealer ID: {dealerId}\n";
            result += $"Direction: {dir}\n";
            result += $"Property Type: {propType}\n";
            result += $"Property Ticker: {offering}\n";
            result += $"Money Type: {moneyType}\n";
            result += $"Money Ticker: {biding}\n";
            result += $"Price: {price}\n";
            result += $"Amount: {amount}\n";
            result += $"Seller Collateral: {cltamt}\n";
            result += $"limitMin: {limitMin}\n";
            result += $"limitMax: {limitMax}\n";
            result += $"Pay By: {string.Join(", ", payBy)}\n";
            return result;
        }
    }
}
