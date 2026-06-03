using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace winconky
{
    public class CryptoData
    {
        public string Btc { get; set; } = "";
        public string Eth { get; set; } = "";
        public string Sol { get; set; } = "";
    }
    public static class CryptoService
    {
        private static readonly HttpClient _http = new HttpClient();

        public static async Task<CryptoData> FetchAsync()
        {
            var data = new CryptoData();

            try { data.Btc = await GetCoinbase("BTC-USD", "BTC"); } catch { data.Btc = "BTC: N/A"; }
            try { data.Eth = await GetCoinbase("ETH-USD", "ETH"); } catch { data.Eth = "ETH: N/A"; }
            try { data.Sol = await GetCoinbase("SOL-USD", "SOL"); } catch { data.Sol = "SOL: N/A"; }

            return data;
        }

        private static async Task<string> GetCoinbase(string pair, string label)
        {
            var json = await _http.GetStringAsync($"https://api.coinbase.com/v2/prices/{pair}/spot");
            var doc = JsonDocument.Parse(json);
            var amount = doc.RootElement.GetProperty("data").GetProperty("amount").GetString();
            return $"{label}: ${amount}";
        }
    }
}