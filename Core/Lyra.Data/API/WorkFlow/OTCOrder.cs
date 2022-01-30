﻿using Lyra.Core.API;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lyra.Data.API.WorkFlow
{
    public class OTCOrder
    {
        // data
        public string daoid { get; set; }   // DAO account ID
        public TradeDirection dir { get; set; }
        public string crypto { get; set; }
        public string fiat { get; set; }
        public PriceType priceType { get; set; }
        public decimal price { get; set; }
        public decimal amount { get; set; }
        public decimal limitmin { get; set; }
        public decimal limitmax { get; set; }
        public string[] payby { get; set; }
        public decimal collateral { get; set; }


        public override bool Equals(object obOther)
        {
            if (null == obOther)
                return false;

            if (object.ReferenceEquals(this, obOther))
              return true;

            if (this.GetType() != obOther.GetType())
                return false;

            var ob = obOther as OTCOrder;
            return daoid == ob.daoid &&
                dir == ob.dir &&
                crypto == ob.crypto &&
                fiat == ob.fiat &&
                priceType == ob.priceType &&
                amount == ob.amount &&
                collateral == ob.collateral &&
                price == ob.price;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(daoid, dir, crypto, fiat, price, priceType, collateral);
        }

        public string GetExtraData()
        {
            string extraData = "";
            extraData += daoid + "|";
            extraData += $"{dir}|";
            extraData += $"{crypto}|";
            extraData += $"{fiat}|";
            extraData += $"{priceType}|";
            extraData += $"{price.ToBalanceLong()}|";
            extraData += $"{amount.ToBalanceLong()}|";
            extraData += $"{collateral.ToBalanceLong()}|";
            return extraData;
        }

        public override string ToString()
        {
            string result = base.ToString();
            result += $"DAO ID: {daoid}\n";
            result += $"Direction: {dir}\n";
            result += $"Crypto: {crypto}\n";
            result += $"Fiat: {fiat}\n";
            result += $"Price Type: {priceType}\n";
            result += $"Price: {price}\n";
            result += $"Amount: {amount}\n";
            result += $"Seller Collateral: {collateral}\n";
            return result;
        }
    }
}
