﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.Logging;
using OsEngine.Market.Servers.Entity;
using OsEngine.Market.Servers.Huobi.Entities;
using OsEngine.Market.Servers.Huobi.Futures.Entities;
using OsEngine.Market.Servers.Huobi.Request;
using OsEngine.Market.Servers.Huobi.Response;
using OsEngine.Market.Servers.Huobi.Spot;
using OsEngine.Market.Services;


namespace OsEngine.Market.Servers.Huobi.Futures
{
    public class HuobiFuturesServer : AServer
    {
        public HuobiFuturesServer()
        {
            HuobiFuturesServerRealization realization = new HuobiFuturesServerRealization("api.hbdm.com",
                "/notification");

            ServerRealization = realization;

            CreateParameterString(OsLocalization.Market.ServerParamPublicKey, "");
            CreateParameterPassword(OsLocalization.Market.ServerParamSecretKey, "");
        }

        /// <summary>
        /// instrument history query
        /// запрос истории по инструменту
        /// </summary>
        public List<Candle> GetCandleHistory(string nameSec, TimeSpan tf)
        {
            return ((HuobiFuturesServerRealization)ServerRealization).GetCandleHistory(nameSec, tf);
        }
    }
    public class HuobiFuturesServerRealization : IServerRealization
    {
        private readonly string _host;
        private readonly string _path;

        /// <summary>
        /// словарь таймфреймов, поддерживаемых этой биржей
        /// </summary>
        private readonly Dictionary<int, string> _supportedIntervals;

        public HuobiFuturesServerRealization(string host,
            string path)
        {
            _supportedIntervals = CreateIntervalDictionary();
            ServerStatus = ServerConnectStatus.Disconnect;

            _host = host;
            _path = path;
        }

        //1min, 5min, 15min, 30min, 60min, 4hour, 1day, 1mon, 1week, 1year
        private Dictionary<int, string> CreateIntervalDictionary()
        {
            var dictionary = new Dictionary<int, string>();

            dictionary.Add(1, "1min");
            dictionary.Add(5, "5min");
            dictionary.Add(15, "15min");
            dictionary.Add(30, "30min");
            dictionary.Add(60, "60min");
            dictionary.Add(240, "4hour");
            dictionary.Add(1440, "1day");
            dictionary.Add(43200, "1mon");
            dictionary.Add(525600, "1year");

            return dictionary;
        }

        /// <summary>
        /// server type
        /// тип сервера
        /// </summary>
        public ServerType ServerType => ServerType.HuobiFutures;
        public ServerConnectStatus ServerStatus { get; set; }
        public List<IServerParameter> ServerParameters { get; set; }
        public DateTime ServerTime { get; set; }

        private string _publicKey;
        private string _secretKey;

        private PublicUrlBuilder _urlBuilder;

        private PrivateUrlBuilder _privateUriBuilder;

        private CancellationTokenSource _cancelTokenSource;

        private Signer _signer;

        private WsSource _wsSource;

        private WsSource _marketDataSource;

        private readonly ConcurrentQueue<string> _queueMessagesReceivedFromExchange = new ConcurrentQueue<string>();

        private readonly ConcurrentQueue<string> _queueMarketDataReceivedFromExchange = new ConcurrentQueue<string>();


        public void Connect()
        {
            _publicKey = ((ServerParameterString)ServerParameters[0]).Value;
            _secretKey = ((ServerParameterPassword)ServerParameters[1]).Value;

            _urlBuilder = new PublicUrlBuilder(_host);
            _privateUriBuilder = new PrivateUrlBuilder(_publicKey, _secretKey, _host);

            _cancelTokenSource = new CancellationTokenSource();

            StartMessageReader();

            StartMarketDataReader();

            _signer = new Signer(_secretKey);

            _wsSource = new WsSource("wss://" + _host + _path);
            _wsSource.MessageEvent += WsSourceOnMessageEvent;
            _wsSource.ByteDataEvent += WsSourceOnByteDataEvent;
            _wsSource.Start();

            _marketDataSource = new WsSource("wss://" + _host + "/ws");
            _marketDataSource.ByteDataEvent += MarketDataSourceOnMessageEvent;
            _marketDataSource.Start();
            ServerStatus = ServerConnectStatus.Connect;
        }

        private void WsSourceOnMessageEvent(WsMessageType msgType, string message)
        {
            switch (msgType)
            {
                case WsMessageType.Opened:
                    Sign();
                    ConnectEvent();
                    StartPortfolioRequester();
                    break;
                case WsMessageType.Closed:
                    DisconnectEvent();
                    break;
                case WsMessageType.StringData:
                    _queueMessagesReceivedFromExchange.Enqueue(message);
                    break;
                case WsMessageType.Error:
                    SendLogMessage(message, LogMessageType.Error);
                    break;
                default:
                    throw new NotSupportedException(message);
            }
        }

        private void WsSourceOnByteDataEvent(WsMessageType msgType, byte[] data)
        {
            switch (msgType)
            {
                case WsMessageType.ByteData:
                    string message = GZipDecompresser.Decompress(data);
                    _queueMessagesReceivedFromExchange.Enqueue(message);
                    break;
                default:
                    throw new NotSupportedException(data.ToString());
            }
        }

        private void StartMessageReader()
        {
            Task.Run(() => MessageReader(_cancelTokenSource.Token), _cancelTokenSource.Token);
        }

        private async void MessageReader(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!_queueMessagesReceivedFromExchange.IsEmpty)
                    {
                        string mes;

                        if (_queueMessagesReceivedFromExchange.TryDequeue(out mes))
                        {
                            dynamic jt = JToken.Parse(mes);

                            string channel = jt.op;

                            if (string.IsNullOrEmpty(channel))
                            {
                                continue;
                            }

                            if (channel == "ping")
                            {
                                var pingMessage = JsonConvert.DeserializeObject<FuturesPing>(mes);

                                if (pingMessage != null && pingMessage.ts != 0)
                                {
                                    long ts = pingMessage.ts;

                                    string pongData = $"{{\"op\": \"pong\", \"ts\": {ts} }}";
                                    _wsSource.SendMessage(pongData);
                                }
                            }
                            else if (channel == "auth")
                            {
                                SendLogMessage("Successful authentication in the system", LogMessageType.Connect);
                            }
                            else if (channel == "notify")
                            {
                                var orderNotify = JsonConvert.DeserializeObject<OrderFuturesNotify>(mes);


                                var order = new Order();

                                order.SecurityNameCode = JoinSecurityName(orderNotify.symbol, orderNotify.contract_type);
                                order.Side = orderNotify.direction == "buy" ? Side.Buy : Side.Sell;
                                order.NumberMarket = orderNotify.order_id.ToString();
                                order.NumberUser = orderNotify.client_order_id ?? 0;
                                order.PortfolioNumber = _portfolioCurrent;

                                order.Price = orderNotify.price;
                                order.Volume = orderNotify.volume;
                                order.State = OrderStateType.Activ;
                                order.TimeCallBack = TimeManager.GetDateTimeFromTimeStamp(orderNotify.ts);

                                if (order.NumberUser != 0)
                                {
                                    MyOrderEvent(order);
                                }

                                foreach (var tradeNotify in orderNotify.trade)
                                {
                                    var security = JoinSecurityName(orderNotify.symbol, orderNotify.contract_type);
                                    var myTrade = CreateMyTrade(security, orderNotify.order_id.ToString(),
                                        orderNotify.direction, tradeNotify);

                                    MyTradeEvent(myTrade);
                                }
                            }
                            else
                            {
                                SendLogMessage(mes, LogMessageType.System);
                            }
                        }
                    }
                    else
                    {
                        await Task.Delay(20);
                    }
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    SendLogMessage("MessageReader error: " + exception, LogMessageType.Error);
                }
            }
        }

        private MyTrade CreateMyTrade(string security, string numberOrderParent, string side, TradeNotify data)
        {
            var trade = new MyTrade();
            trade.SecurityNameCode = security;
            trade.NumberOrderParent = numberOrderParent;
            trade.Volume = data.trade_volume;
            trade.Price = data.trade_price;
            trade.NumberTrade = data.id;
            trade.Time = TimeManager.GetDateTimeFromTimeStamp(data.created_at);
            trade.Side = side == "buy" ? Side.Buy : Side.Sell;

            return trade;
        }

        #region Разбор рыночных данных

        private DateTime _lastTimeUpdateSocket;

        private async void SourceAliveCheckerThread(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(200);
                if (_lastTimeUpdateSocket == DateTime.MinValue)
                {
                    continue;
                }

                if (_lastTimeUpdateSocket.AddSeconds(60) < DateTime.Now)
                {
                    SendLogMessage("The websocket is disabled. Restart", LogMessageType.Error);

                    if (ServerStatus != ServerConnectStatus.Disconnect)
                    {
                        ServerStatus = ServerConnectStatus.Disconnect;
                        DisconnectEvent();
                    }

                    Dispose();
                    return;
                }
            }
        }

        private void MarketDataSourceOnMessageEvent(WsMessageType msgType, byte[] data)
        {
            switch (msgType)
            {
                case WsMessageType.Opened:
                    break;
                case WsMessageType.Closed:
                    DisconnectEvent();
                    break;
                case WsMessageType.ByteData:
                    string message = GZipDecompresser.Decompress(data);
                    _queueMarketDataReceivedFromExchange.Enqueue(message);
                    break;
                case WsMessageType.Error:
                    break;
                default:
                    throw new NotSupportedException("");
            }
        }

        private void StartMarketDataReader()
        {
            Task.Run(() => MarketDataReader(_cancelTokenSource.Token), _cancelTokenSource.Token);
            Task.Run(() => SourceAliveCheckerThread(_cancelTokenSource.Token), _cancelTokenSource.Token);
        }

        private async void MarketDataReader(CancellationToken token)
        {
            _lastTimeUpdateSocket = DateTime.MinValue;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!_queueMarketDataReceivedFromExchange.IsEmpty)
                    {
                        string mes;

                        if (_queueMarketDataReceivedFromExchange.TryDequeue(out mes))
                        {
                            var pingMessage = JsonConvert.DeserializeObject<MarketDataPingMessage>(mes);

                            if (pingMessage != null && pingMessage.ping != 0)
                            {
                                string pongData = $"{{\"pong\":{pingMessage.ping}}}";
                                _marketDataSource.SendMessage(pongData);
                            }
                            else
                            {
                                if (mes.StartsWith("{\"ch\":\"market."))
                                {
                                    var security = GetSecurityName(mes, out var channel);

                                    if (channel == "trade")
                                    {
                                        _lastTimeUpdateSocket = DateTime.Now;
                                        var response = JsonConvert.DeserializeObject<TradeInfo>(mes);
                                        foreach (var trade in CreateTrades(security, response))
                                        {
                                            NewTradesEvent(trade);
                                        }
                                    }
                                    else if (channel == "depth")
                                    {
                                        _lastTimeUpdateSocket = DateTime.Now;
                                        var response = JsonConvert.DeserializeObject<SubscribeDepthResponse>(mes);
                                        MarketDepthEvent(CreateMarketDepth(security, response));
                                    }
                                }
                                else if (mes.StartsWith("{\"id\":\"\",\"rep\":\"market."))
                                {
                                    var response = JsonConvert.DeserializeObject<GetCandlestickResponse>(mes);

                                    response.security = response.GetSecurity();

                                    _allCandleSeries.Add(response);
                                }
                                else
                                {
                                    SendLogMessage(mes, LogMessageType.System);
                                }
                            }
                        }
                    }
                    else
                    {
                        await Task.Delay(20);
                    }
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    SendLogMessage("MessageReader error: " + exception, LogMessageType.Error);
                }
            }
        }

        private string GetSecurityName(string data, out string channel)
        {
            int firstIndex = data.IndexOf(':');
            int secondIndex = data.IndexOf(',');

            var substring = data.Substring(firstIndex, secondIndex - firstIndex);

            var parts = substring.Split('.');

            channel = parts[2];

            var security = parts[1];

            return security;
        }

        private List<Trade> CreateTrades(string securityName, TradeInfo response)
        {
            var trades = new List<Trade>();

            var data = response.tick.data;

            for (int i = 0; i < data.Count; i++)
            {
                var trade = new Trade();
                trade.SecurityNameCode = securityName;
                trade.Id = data[i].id.ToString();
                trade.Side = data[i].direction == "buy" ? Side.Buy : Side.Sell;
                trade.Price = data[i].price;
                trade.Volume = data[i].amount;
                trade.Time = TimeManager.GetDateTimeFromTimeStamp(data[i].ts);

                trades.Add(trade);
            }

            return trades;
        }

        private MarketDepth CreateMarketDepth(string securityName, SubscribeDepthResponse response)
        {
            var newDepth = new MarketDepth();

            newDepth.Time = TimeManager.GetDateTimeFromTimeStamp(response.tick.ts);

            newDepth.SecurityNameCode = securityName;

            var bids = response.tick.bids;

            var asks = response.tick.asks;

            foreach (var bid in bids)
            {
                newDepth.Bids.Add(new MarketDepthLevel()
                {
                    Price = bid[0],
                    Bid = bid[1],
                });
            }

            foreach (var ask in asks)
            {
                newDepth.Asks.Add(new MarketDepthLevel()
                {
                    Price = ask[0],
                    Ask = ask[1],
                });
            }

            return newDepth;
        }

        #endregion

        public void Dispose()
        {
            try
            {
                if (_wsSource != null)
                {
                    UnInitialize();
                }

                if (_cancelTokenSource != null && !_cancelTokenSource.IsCancellationRequested)
                {
                    _cancelTokenSource.Cancel();
                }

                _allCandleSeries?.Clear();
            }
            catch (Exception e)
            {
                SendLogMessage("Huobi dispose error: " + e, LogMessageType.Error);
            }

            ServerStatus = ServerConnectStatus.Disconnect;
        }

        private void UnInitialize()
        {
            _wsSource.Dispose();
            _wsSource.MessageEvent -= WsSourceOnMessageEvent;
            _wsSource.ByteDataEvent -= WsSourceOnByteDataEvent;
            _wsSource = null;

            _marketDataSource.Dispose();
            _marketDataSource.ByteDataEvent -= MarketDataSourceOnMessageEvent;
            _marketDataSource = null;
        }

        #region Запросы

        List<Security> _securities = new List<Security>();

        public void GetSecurities()
        {
            string url = _urlBuilder.Build("/api/v1/contract_contract_info");
            var httpClient = new HttpClient();
            string response = httpClient.GetStringAsync(url).Result;

            _securities = CreateSecurities(response);

            SecurityEvent(_securities);
        }

        private List<Security> CreateSecurities(string data)
        {
            FuturesSymbolResponse symbols = JsonConvert.DeserializeObject<FuturesSymbolResponse>(data);

            var securities = new List<Security>();

            foreach (var symbol in symbols.Data)
            {
                try
                {
                    var security = new Security();

                    security.Name = JoinSecurityName(symbol.Symbol, symbol.ContractType);
                    security.NameFull = symbol.Symbol;
                    security.NameClass = symbol.ContractType;
                    security.NameId = JoinSecurityName(symbol.Symbol, symbol.ContractType);
                    security.SecurityType = SecurityType.Futures;
                    security.Decimals = symbol.PriceTick.ToString(CultureInfo.CurrentCulture).DecimalsCount();
                    security.PriceStep = symbol.PriceTick;
                    security.PriceStepCost = security.PriceStep;
                    security.State = SecurityStateType.Activ;
                    security.Lot = 1;

                    securities.Add(security);
                }
                catch (Exception error)
                {
                    throw new Exception("Security creation error \n" + error.ToString());
                }
            }

            return securities;
        }

        private string JoinSecurityName(string symbol, string contractType)
        {
            string postfix = "_CQ";

            if (contractType == "this_week")
            {
                postfix = "_CW";
            }
            else if (contractType == "next_week")
            {
                postfix = "_NW";
            }
            return symbol + postfix;
        }

        private void StartPortfolioRequester()
        {
            Task.Run(() => PortfolioRequester(_cancelTokenSource.Token), _cancelTokenSource.Token);
        }

        public List<Portfolio> Portfolios;

        private async void PortfolioRequester(CancellationToken token)
        {
            if (Portfolios == null)
            {
                Portfolios = new List<Portfolio>();

                var portfolio = new Portfolio();
                portfolio.Number = "HuobiFutures";
                portfolio.ValueBegin = 1;
                portfolio.ValueCurrent = 1;
                portfolio.ValueBlocked = 1;

                Portfolios.Add(portfolio);

                PortfolioEvent(Portfolios);
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, token);

                    GetPortfolios();
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    SendLogMessage("MessageReader error: " + exception, LogMessageType.Error);
                }
            }
        }

        public void GetPortfolios()
        {
            if (Portfolios == null)
            {
                return;
            }
            foreach (var portfolio in Portfolios)
            {
                string url = _privateUriBuilder.Build("POST", "/api/v1/contract_account_info");

                StringContent httpContent = new StringContent("", Encoding.UTF8, "application/json");

                var httpClient = new HttpClient();

                var response = httpClient.PostAsync(url, httpContent).Result;

                string result = response.Content.ReadAsStringAsync().Result;

                if (result.Contains("Incorrect Access key"))
                {
                    SendLogMessage("Huobi: Incorrect Access API key", LogMessageType.Error);
                    return;
                }

                FuturesAccountInfo accountInfo = JsonConvert.DeserializeObject<FuturesAccountInfo>(result);

                portfolio.ClearPositionOnBoard();

                for (int i = 0; accountInfo.data != null && i < accountInfo.data.Count; i++)
                {
                    var currentData = accountInfo.data[i];

                    PositionOnBoard pos = new PositionOnBoard();
                    pos.SecurityNameCode = currentData.margin_asset;
                    pos.ValueBegin = currentData.margin_static.ToDecimal();
                    pos.ValueCurrent = currentData.margin_static.ToDecimal();
                    pos.ValueBlocked = currentData.margin_static.ToDecimal();

                    portfolio.SetNewPosition(pos);
                }
            }

            PortfolioEvent(Portfolios);
        }

        private string _portfolioCurrent;

        public void SendOrder(Order order)
        {
            _portfolioCurrent = order.PortfolioNumber;

            Dictionary<string, dynamic> jsonContent = new Dictionary<string, dynamic>();

            Security mySec = null;

            for (int i = 0; i < _securities.Count; i++)
            {
                if (_securities[i].Name == order.SecurityNameCode)
                {
                    mySec = _securities[i];
                    break;
                }
            }

            string[] contractData = mySec.NameId.Split('_');

            var contractType = "quarter";

            if (contractData[1] == "CW")
            {
                contractType = "this_week";
            }
            else if (contractData[1] == "NW")
            {
                contractType = "next_week";
            }

            jsonContent.Add("symbol", contractData[0]);
            jsonContent.Add("contract_type", contractType);
            jsonContent.Add("client_order_id", order.NumberUser);
            jsonContent.Add("price", order.Price);
            jsonContent.Add("volume", order.Volume.ToString().Replace(",", "."));
            jsonContent.Add("direction", order.Side == Side.Buy ? "buy" : "sell");

            // если ордер открывающий позицию - тут "open", если закрывающий - "close"
            if (order.PositionConditionType == OrderPositionConditionType.Close)
            {
                jsonContent.Add("offset", "close");
            }
            else
            {
                jsonContent.Add("offset", "open");
            }

            jsonContent.Add("lever_rate", "10");
            jsonContent.Add("order_price_type", "limit");
            jsonContent.Add("channel_code", "AAe2ccbd47");

            string url = _privateUriBuilder.Build("POST", "/api/v1/contract_order");

            StringContent httpContent = new StringContent(JsonConvert.SerializeObject(jsonContent), Encoding.UTF8, "application/json");

            var httpClient = new HttpClient();

            var response = httpClient.PostAsync(url, httpContent).Result;

            string result = response.Content.ReadAsStringAsync().Result;

            PlaceFuturesOrderResponse orderResponse = JsonConvert.DeserializeObject<PlaceFuturesOrderResponse>(result);

            if (orderResponse.status == "ok")
            {
                SendLogMessage($"Order num {order.NumberUser} on exchange.", LogMessageType.Trade);
            }
            else
            {
                //err_msg
                dynamic errorData = JToken.Parse(result);
                string errorMsg = errorData.err_msg;

                SendLogMessage($"Order exchange error num {order.NumberUser} : {errorMsg}", LogMessageType.Error);

                order.State = OrderStateType.Fail;

                MyOrderEvent(order);
            }
        }

        public void CancelOrder(Order order)
        {
            Dictionary<string, dynamic> jsonContent = new Dictionary<string, dynamic>();

            var contractData = order.SecurityNameCode.Split('_');

            jsonContent.Add("order_id", order.NumberMarket);
            jsonContent.Add("client_order_id", order.NumberUser);
            jsonContent.Add("symbol", contractData[0]);

            string url = _privateUriBuilder.Build("POST", "/api/v1/contract_cancel");

            StringContent httpContent = new StringContent(JsonConvert.SerializeObject(jsonContent), Encoding.UTF8, "application/json");

            var httpClient = new HttpClient();

            var response = httpClient.PostAsync(url, httpContent).Result;

            string result = response.Content.ReadAsStringAsync().Result;

            CancelFuturesOrderResponse cancelResponse = JsonConvert.DeserializeObject<CancelFuturesOrderResponse>(result);

            if (cancelResponse.status == "ok")
            {
                SendLogMessage($"Order num {order.NumberUser} canceled.", LogMessageType.Trade);
                order.State = OrderStateType.Cancel;
                MyOrderEvent(order);
            }
            else
            {
                SendLogMessage($"Error on order cancel num {order.NumberUser} : {cancelResponse.data.errors}", LogMessageType.Error);
            }
        }

        public void Subscrible(Security security)
        {
            string topic = $"market.{security.NameId}.trade.detail";

            string clientId = "";

            _marketDataSource.SendMessage($"{{ \"sub\": \"{topic}\",\"id\": \"{clientId}\" }}");

            topic = $"market.{security.NameId}.depth.step0";

            _marketDataSource.SendMessage($"{{ \"sub\": \"{topic}\",\"id\": \"{clientId}\" }}");

            topic = $"orders.{security.NameFull}";

            _wsSource.SendMessage($"{{\"op\":\"sub\", \"topic\":\"{topic}\", \"cid\": \"{clientId}\" }}");
        }

        public List<Trade> GetTickDataToSecurity(Security security, DateTime startTime, DateTime endTime, DateTime actualTime)
        {
            return null;
        }

        public void GetOrdersState(List<Order> orders)
        {

        }

        private readonly List<GetCandlestickResponse> _allCandleSeries = new List<GetCandlestickResponse>();

        public List<Candle> GetCandleDataToSecurity(Security security, TimeFrameBuilder timeFrameBuilder, DateTime startTime, DateTime endTime,
            DateTime actualTime)
        {
            List<Candle> candles = new List<Candle>();

            int oldInterval = Convert.ToInt32(timeFrameBuilder.TimeFrameTimeSpan.TotalMinutes);

            var step = new TimeSpan(0, (int)(oldInterval * 1000), 0);

            actualTime = startTime;

            var midTime = actualTime + step;

            while (true)
            {
                if (actualTime >= endTime)
                {
                    break;
                }

                string name = security.NameId;

                if (security.NameId.Contains("CW") ||
                        security.NameId.Contains("NW") ||
                        security.NameId.Contains("CQ") ||
                        security.NameId.Contains("NQ"))
                {
                    name = security.Name;
                }

                List<Candle> newCandles = GetCandlesByRest(oldInterval, name, actualTime, midTime);

                if (candles.Count != 0 && newCandles != null && newCandles.Count != 0)
                {
                    for (int i = 0; i < newCandles.Count; i++)
                    {
                        if (candles[candles.Count - 1].TimeStart >= newCandles[i].TimeStart)
                        {
                            newCandles.RemoveAt(i);
                            i--;
                        }
                    }
                }

                if (newCandles == null ||
                    newCandles.Count == 0)
                {
                    actualTime = actualTime.AddDays(5);
                    midTime = actualTime + step;
                    continue;
                }

                candles.AddRange(newCandles);

                actualTime = candles[candles.Count - 1].TimeStart;
                midTime = actualTime + step;
            }

            if (candles.Count == 0)
            {
                return null;
            }

            return candles;
        }

        /// <summary>
        /// request instrument history
        /// запрос истории по инструменту
        /// </summary>
        public List<Candle> GetCandleHistory(string nameSec, TimeSpan tf)
        {
            var diff = new TimeSpan(0, (int)(tf.TotalMinutes * 2000), 0);

            return GetCandles((int)tf.TotalMinutes, nameSec, DateTime.UtcNow - diff, DateTime.UtcNow);
        }

        private object _locker = new object();
        //

        private List<Candle> GetCandlesByRest(int oldInterval, string security, DateTime startTime, DateTime endTime)
        {
            //https://api.hbdm.com/market/history/kline?period=1min&size=200&symbol=BTC_CQ

            string url = "https://api.hbdm.com/market/history/kline?";

            string needIntervalForQuery =
            CandlesCreator.DetermineAppropriateIntervalForRequest(oldInterval, _supportedIntervals, out var needInterval);

            url += "period=" + needIntervalForQuery + "&";
            url += "size=1000&";

            var from = TimeManager.GetTimeStampSecondsToDateTime(startTime);
            var to = TimeManager.GetTimeStampSecondsToDateTime(endTime);

            url += "from=" + from + "&";
            //url += "to=" + to + "&";
            url += "symbol=" + security;

            var httpClient = new HttpClient();

            string resp = httpClient.GetStringAsync(url).Result;

            var response = JsonConvert.DeserializeObject<GetCandlestickResponse>(resp);

            if (response.status == "error")
            {
                return null;
            }

            var candles = CreateCandlesFromJson(response);

            return candles;
        }

        private List<Candle> GetCandles(int oldInterval, string security, DateTime startTime, DateTime endTime)
        {
            lock (_locker)
            {

                var needIntervalForQuery =
                    CandlesCreator.DetermineAppropriateIntervalForRequest(oldInterval, _supportedIntervals, out var needInterval);

                var clientId = "";

                string topic = $"market.{security}.kline.{needIntervalForQuery}";

                var from = TimeManager.GetTimeStampSecondsToDateTime(startTime);
                var to = TimeManager.GetTimeStampSecondsToDateTime(endTime);

                //https://api.hbdm.com/market/history/kline?period=1min&size=200&symbol=BTC_CQ

                string request = $"{{ \"req\": \"{topic}\",\"id\": \"{clientId}\", \"from\":{from}, \"to\":{to} }}";

                _marketDataSource.SendMessage(request);

                var startLoadingTime = DateTime.Now;

                while (startLoadingTime.AddSeconds(40) > DateTime.Now)
                {
                    var candles = _allCandleSeries.Find(s => s.security == security && s.GetTimeFrame() == needIntervalForQuery);

                    if (candles != null)
                    {
                        _allCandleSeries.Remove(candles);

                        var oldCandles = CreateCandlesFromJson(candles);

                        if (oldInterval == needInterval)
                        {
                            return oldCandles;
                        }

                        var newCandles = CandlesCreator.CreateCandlesRequiredInterval(needInterval, oldInterval, oldCandles);

                        return newCandles;
                    }

                    Thread.Sleep(100);
                }

                SendLogMessage(OsLocalization.Market.Message95 + security, LogMessageType.Error);

                return null;
            }
        }

        private List<Candle> CreateCandlesFromJson(GetCandlestickResponse rawCandles)
        {
            var candles = new List<Candle>();

            foreach (var jtCandle in rawCandles.data)
            {
                var candle = new Candle();

                candle.TimeStart = TimeManager.GetDateTimeFromTimeStampSeconds(jtCandle.id);
                candle.Open = jtCandle.open;
                candle.High = jtCandle.high;
                candle.Low = jtCandle.low;
                candle.Close = jtCandle.close;
                candle.Volume = jtCandle.vol;

                candles.Add(candle);
            }

            return candles;
        }

        #endregion

        private void Sign()
        {
            string authRequest = BuildSign(DateTime.UtcNow);
            _wsSource.SendMessage(authRequest);
        }

        public string BuildSign(DateTime utcDateTime)
        {
            string strDateTime = utcDateTime.ToString("s");

            var request = new GetRequest()
                .AddParam("AccessKeyId", _publicKey)
                .AddParam("SignatureMethod", "HmacSHA256")
                .AddParam("SignatureVersion", "2")
                .AddParam("Timestamp", strDateTime);

            string signature = _signer.Sign("GET", _host, _path, request.BuildParams());

            var auth = new WebSocketAuthenticationRequestFutures
            {
                AccessKeyId = _publicKey,
                Timestamp = strDateTime,
                Signature = signature
            };

            return auth.ToJson();
        }

        public void SendLogMessage(string messgae, LogMessageType logMessageType)
        {
            LogMessageEvent(messgae, logMessageType);
        }


        public event Action<Order> MyOrderEvent;
        public event Action<MyTrade> MyTradeEvent;
        public event Action<List<Portfolio>> PortfolioEvent;
        public event Action<List<Security>> SecurityEvent;
        public event Action<MarketDepth> MarketDepthEvent;
        public event Action<Trade> NewTradesEvent;
        public event Action ConnectEvent;
        public event Action DisconnectEvent;
        public event Action<string, LogMessageType> LogMessageEvent;

        public void CancelAllOrders()
        {
            
        }

        public void CancelAllOrdersToSecurity(Security security)
        {

        }

        public void ResearchTradesToOrders(List<Order> orders)
        {
           
        }

        public List<Candle> GetLastCandleHistory(Security security, TimeFrameBuilder timeFrameBuilder, int candleCount)
        {
            throw new NotImplementedException();
        }
    }

    public class WebSocketAuthenticationRequestFutures
    {
        public string op { get { return "auth"; } }
        public string type { get { return "api"; } }

        public string AccessKeyId;
        public string SignatureMethod { get { return "HmacSHA256"; } }
        public string SignatureVersion { get { return "2"; } }
        public string Timestamp;
        public string Signature;

        public string ToJson()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}