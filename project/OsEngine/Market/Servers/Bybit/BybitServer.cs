﻿using Newtonsoft.Json.Linq;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.Logging;
using OsEngine.Market.Servers.Bybit.Entities;
using OsEngine.Market.Servers.Bybit.EntityCreators;
using OsEngine.Market.Servers.Bybit.Utilities;
using OsEngine.Market.Servers.Entity;
using OsEngine.Market.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Net;
using System.Net.Http;
using Newtonsoft.Json;

namespace OsEngine.Market.Servers.Bybit
{
    public class BybitServer : AServer
    {
        public BybitServer()
        {
            BybitServerRealization realization = new BybitServerRealization();
            ServerRealization = realization;

            CreateParameterString(OsLocalization.Market.ServerParamPublicKey, "");
            CreateParameterPassword(OsLocalization.Market.ServerParamSecretKey, "");
            CreateParameterEnum("Futures Type", "USDT Perpetual", new List<string> { "USDT Perpetual", "Inverse Perpetual" });
            CreateParameterEnum("Net Type", "MainNet", new List<string> { "MainNet", "TestNet" });
            CreateParameterEnum("Position Mode", "Merged Single", new List<string> { "Merged Single", "Both Side" });
        }

        /// <summary>
        /// instrument history query
        /// запрос истории по инструменту
        /// </summary>
        public List<Candle> GetCandleHistory(string nameSec, TimeSpan tf)
        {
            return ((BybitServerRealization)ServerRealization).GetCandleHistory(nameSec, tf);
        }
    }

    public class BybitServerRealization : IServerRealization
    {
        #region Properties
        public ServerType ServerType => ServerType.Bybit;

        public ServerConnectStatus ServerStatus { get; set; }
        public List<IServerParameter> ServerParameters { get; set; }
        public DateTime ServerTime { get; set; }
        #endregion

        #region Fields

        private string public_key;
        private string secret_key;

        private Client client;
        private WsSource ws_source_public;
        private WsSource ws_source_private;

        private DateTime last_time_update_socket;

        private string futures_type = "USDT Perpetual";
        private string net_type = "MainNet";
        private string hedge_mode = "Merged Single";

        private readonly Dictionary<int, string> supported_intervals;
        public List<Portfolio> Portfolios;
        private CancellationTokenSource cancel_token_source;
        private readonly ConcurrentQueue<string> queue_messages_received_from_fxchange;
        private readonly Dictionary<string, Action<JToken>> response_handlers;
        private object locker_candles = new object();
        private BybitMarketDepthCreator market_mepth_creator;
        #endregion

        #region Constructor
        public BybitServerRealization() : base()
        {
            queue_messages_received_from_fxchange = new ConcurrentQueue<string>();

            supported_intervals = CreateIntervalDictionary();
            ServerStatus = ServerConnectStatus.Disconnect;

            response_handlers = new Dictionary<string, Action<JToken>>();
            response_handlers.Add("auth", HandleAuthMessage);
            response_handlers.Add("ping", HandlePingMessage);
            response_handlers.Add("subscribe", HandleSubscribeMessage);
            response_handlers.Add("orderBookL2_25", HandleorderBookL2_25Message);
            response_handlers.Add("trade", HandleTradesMessage);
            response_handlers.Add("execution", HandleMyTradeMessage);
            response_handlers.Add("order", HandleOrderMessage);
        }
        #endregion

        #region Service
        private Dictionary<int, string> CreateIntervalDictionary()
        {
            var dictionary = new Dictionary<int, string>();

            dictionary.Add(1, "1");
            dictionary.Add(3, "3");
            dictionary.Add(5, "5");
            dictionary.Add(15, "15");
            dictionary.Add(30, "30");
            dictionary.Add(60, "60");
            dictionary.Add(120, "120");
            dictionary.Add(240, "240");
            dictionary.Add(360, "360");
            dictionary.Add(720, "720");
            dictionary.Add(1440, "D");

            return dictionary;
        }
        #endregion

        public void Connect()
        {
            public_key = ((ServerParameterString)ServerParameters[0]).Value;
            secret_key = ((ServerParameterPassword)ServerParameters[1]).Value;

            futures_type = ((ServerParameterEnum)ServerParameters[2]).Value;
            net_type = ((ServerParameterEnum)ServerParameters[3]).Value;
            hedge_mode = ((ServerParameterEnum)ServerParameters[4]).Value;

            if (futures_type != "Inverse Perpetual" && net_type != "TestNet")
            {
                client = new Client(public_key, secret_key, false, true);
            }

            else if (futures_type != "Inverse Perpetual" && net_type == "TestNet")
            {
                client = new Client(public_key, secret_key, false, false);
            }

            else if (futures_type == "Inverse Perpetual" && net_type != "TestNet")
            {
                client = new Client(public_key, secret_key, true, true);
            }

            else if (futures_type == "Inverse Perpetual" && net_type == "TestNet")
            {
                client = new Client(public_key, secret_key, true, false);
            }

            last_time_update_socket = DateTime.UtcNow;
            cancel_token_source = new CancellationTokenSource();
            market_mepth_creator = new BybitMarketDepthCreator();

            _alreadySubscribleOrders = false;
            _alreadySubscribleTrades = false;
            _alreadySubSec.Clear();

            StartMessageReader();

            ws_source_private = new WsSource(client.WsPrivateUrl);
            ws_source_private.MessageEvent += WsSourceOnMessageEvent;
            ws_source_private.Start();

            ws_source_public = new WsSource(client.WsPublicUrl);
            ws_source_public.MessageEvent += WsSourceOnMessageEvent;
            ws_source_public.Start();
            ServerStatus = ServerConnectStatus.Connect;
        }

        public void Dispose()
        {
            try
            {
                if (ws_source_private != null)
                {
                    ws_source_private.MessageEvent -= WsSourceOnMessageEvent;
                    ws_source_private.Dispose();
                    ws_source_private = null;
                }

                if (ws_source_public != null)
                {
                    ws_source_public.MessageEvent -= WsSourceOnMessageEvent;
                    ws_source_public.Dispose();
                    ws_source_public = null;
                }

                if (cancel_token_source != null &&
                    !cancel_token_source.IsCancellationRequested)
                {
                    cancel_token_source.Cancel();
                    cancel_token_source = null;
                }

                cancel_token_source = new CancellationTokenSource();
                market_mepth_creator = new BybitMarketDepthCreator();

                client = null;

                _alreadySubSec.Clear();

                _alreadySubscribleOrders = false;
                _alreadySubscribleTrades = false;

                DisconnectEvent();
            }
            catch (Exception e)
            {
                SendLogMessage("Bybit dispose error: " + e.Message + " " + e.StackTrace, LogMessageType.Error);
            }
            ServerStatus = ServerConnectStatus.Disconnect;
        }

        private void WsSourceOnMessageEvent(WsMessageType message_type, string message)
        {
            if (message_type == WsMessageType.Opened)
            {
                SendLoginMessage();
                ConnectEvent();
                StartPortfolioRequester();
            }
            else if (message_type == WsMessageType.Closed)
            {
                DisconnectEvent();
                Dispose();
            }
            else if (message_type == WsMessageType.StringData)
            {
                queue_messages_received_from_fxchange.Enqueue(message);
            }
            else if (message_type == WsMessageType.Error)
            {
                if (message.Contains("no address was supplied"))
                {
                    DisconnectEvent();
                    Dispose();
                }

                SendLogMessage(message, LogMessageType.Error);
            }
        }

        private void StartMessageReader()
        {
            Task.Run(() => MessageReader(cancel_token_source.Token), cancel_token_source.Token);
            Task.Run(() => SourceAliveCheckerThread(cancel_token_source.Token), cancel_token_source.Token);
        }

        private void StartPortfolioRequester()
        {
            Task.Run(() => PortfolioRequester(cancel_token_source.Token), cancel_token_source.Token);
        }

        private async void MessageReader(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!queue_messages_received_from_fxchange.IsEmpty && queue_messages_received_from_fxchange.TryDequeue(out string mes))
                    {
                        JToken response = JToken.Parse(mes);

                        if (response.First.Path == "success")
                        {

                            bool is_success = response.SelectToken("success").Value<bool>();

                            if (is_success)
                            {
                                string type = JToken.Parse(response.SelectToken("request").ToString()).SelectToken("op").Value<string>();

                                if (response_handlers.ContainsKey(type))
                                {
                                    response_handlers[type].Invoke(response);
                                }
                                else
                                {
                                    SendLogMessage(mes, LogMessageType.System);
                                }
                            }
                            else if (!is_success)
                            {
                                string type = JToken.Parse(response.SelectToken("request").ToString()).SelectToken("op").Value<string>();
                                string error_mssage = response.SelectToken("ret_msg").Value<string>();

                                if (type == "subscribe" && error_mssage.Contains("already"))
                                    continue;


                                SendLogMessage("Broken response success marker " + mes, LogMessageType.Error);
                                if (mes.Contains("\"auth\"") && mes.Contains("error"))
                                {
                                    Dispose();
                                }
                            }
                        }

                        else if (response.First.Path == "topic") //orderBookL2_25.BTCUSD
                        {
                            string type = response.SelectToken("topic").Value<string>().Split('.')[0];

                            if (response_handlers.ContainsKey(type))
                            {
                                response_handlers[type].Invoke(response);
                            }
                            else
                            {
                                SendLogMessage(mes, LogMessageType.System);
                            }
                        }

                        else
                        {
                            SendLogMessage("Broken response topic marker " + mes, LogMessageType.Error);
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

        private async void SourceAliveCheckerThread(CancellationToken token)
        {
            var ping_message = BybitWsRequestBuilder.GetPingRequest();

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(15000);

                ws_source_public?.SendMessage(ping_message);

                if (last_time_update_socket == DateTime.MinValue)
                {
                    continue;
                }
                if (last_time_update_socket.AddSeconds(60) < DateTime.UtcNow)
                {
                    SendLogMessage("The websocket is disabled. Restart", LogMessageType.Error);
                    ConnectEvent();
                    return;
                }
            }
        }


        #region Запросы Rest

        private void SetPositionMode(Security security)
        {
            try
            {
                int mode = hedge_mode.Equals("Merged Single") ? 0 : 3;
                Dictionary<string, string> parameters = new Dictionary<string, string>();
                parameters.Add("mode", $"{mode}");
                parameters.Add("symbol", security.Name);
                parameters.Add("coin", null);
                parameters.Add("api_key", client.ApiKey);
                parameters.Add("recv_window", "90000000");
                DateTime time = GetServerTime();

                var res = CreatePrivatePostQuery(client, "/contract/v3/private/position/switch-mode", parameters, time);

                string json = res.ToString();

                PositionModeResponse posMode = JsonConvert.DeserializeObject<PositionModeResponse>(json);


                if (posMode.retCode.Equals("140025") || posMode.retCode.Equals("0"))
                {
                    return;
                }
                else
                {
                    throw new Exception(posMode.retMsg);
                }
            }
            catch (Exception error)
            {
                SendLogMessage(error.Message, LogMessageType.Error);
            }
        }

        private async void PortfolioRequester(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(10000, token);

                    GetPortfolios();
                    GetPositions();
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    SendLogMessage("Portfolio creator error: " + exception, LogMessageType.Error);
                }
            }
        }

        private void GetPositions()
        {
            try
            {
                // https://api-testnet.bybit.com/private/linear/position/list?api_key={api_key}&symbol=BTCUSDT&timestamp={timestamp}&sign={sign}"

                // /private/linear/position/list

                Dictionary<string, string> parameters = new Dictionary<string, string>();
                parameters.Add("api_key", client.ApiKey);
                parameters.Add("recv_window", "90000000");

                JToken account_response = CreatePrivateGetQuery(client, "/private/linear/position/list", parameters);

                if (account_response == null)
                {
                    return;
                }

                if (_portfolios == null ||
                     _portfolios.Count == 0)
                {
                    return;
                }

                Portfolio myPortf = null;

                for (int i = 0; i < _portfolios.Count; i++)
                {
                    if (_portfolios[i].Number == null)
                    {
                        return;
                    }
                    if (_portfolios[i].Number == "BybitPortfolio")
                    {
                        myPortf = _portfolios[i];
                    }
                }

                if (myPortf == null)
                {
                    return;
                }

                List<PositionOnBoard> poses = BybitPortfolioCreator.CreatePosOnBoard(account_response.SelectToken("result"));

                if (poses == null)
                {
                    return;
                }

                for (int i = 0; i < poses.Count; i++)
                {
                    myPortf.SetNewPosition(poses[i]);
                }

                PortfolioEvent(_portfolios);
            }
            catch (Exception)
            {
                //ignore
            }
        }

        public void GetPortfolios()
        {
            try
            {
                List<Portfolio> portfolios = new List<Portfolio>();

                Dictionary<string, string> parameters = new Dictionary<string, string>();

                parameters.Add("api_key", client.ApiKey);
                parameters.Add("recv_window", "90000000");

                JToken account_response = CreatePrivateGetQuery(client, "/v2/private/wallet/balance", parameters);

                if (account_response == null)
                {
                    return;
                }

                string isSuccessfull = account_response.SelectToken("ret_msg").Value<string>();

                if (isSuccessfull == "OK")
                {
                    portfolios.Add(BybitPortfolioCreator.Create(account_response.SelectToken("result"), "BybitPortfolio"));
                }
                else
                {
                    SendLogMessage($"Can not get portfolios info.", LogMessageType.Error);
                    SendLogMessage(isSuccessfull, LogMessageType.Error);

                    portfolios.Add(BybitPortfolioCreator.Create("undefined"));
                }

                PortfolioEvent(portfolios);
                _portfolios = portfolios;
            }
            catch (Exception)
            {
                //ignore
            }
            
        } // both futures

        List<Portfolio> _portfolios;

        public void GetSecurities() // both futures
        {
            JToken account_response = CreatePublicGetQuery(client, "/v2/public/symbols");

            if (account_response == null)
            {
                return;
            }

            string isSuccessfull = account_response.SelectToken("ret_msg").Value<string>();

            if (isSuccessfull == "OK")
            {
                SecurityEvent(BybitSecurityCreator.Create(account_response.SelectToken("result"), futures_type));
            }
            else
            {
                SendLogMessage($"Can not get securities.", LogMessageType.Error);
            }
        }

        public void SendOrder(Order order)
        {
            if (order.TypeOrder == OrderPriceType.Iceberg)
            {
                SendLogMessage("Bybit does't support iceberg orders", LogMessageType.Error);
                return;
            }

            string side = "Buy";
            if (order.Side == Side.Sell)
                side = "Sell";

            string type = "Limit";
            if (order.TypeOrder == OrderPriceType.Market)
                type = "Market";

            string reduce = "false";
            if (order.PositionConditionType == OrderPositionConditionType.Close)
            {
                reduce = "true";
            }

            Dictionary<string, string> parameters = new Dictionary<string, string>();

            parameters.Add("api_key", client.ApiKey);
            parameters.Add("side", side);
            parameters.Add("order_type", type);
            //parameters.Add("referer", "OsEngine");
            parameters.Add("qty", order.Volume.ToString().Replace(",", "."));
            parameters.Add("time_in_force", "GoodTillCancel");
            parameters.Add("order_link_id", order.NumberUser.ToString());
            parameters.Add("symbol", order.SecurityNameCode);
            parameters.Add("price", order.Price.ToString().Replace(",", "."));
            parameters.Add("reduce_only", reduce);
            parameters.Add("close_on_trigger", "false");
            parameters.Add("recv_window", "90000000");

            JToken place_order_response = null;

            DateTime time = GetServerTime();

            try
            {
                if (client.FuturesMode == "Inverse")
                    place_order_response = CreatePrivatePostQuery(client, "/v2/private/order/create", parameters, time);
                else
                    place_order_response = CreatePrivatePostQuery(client, "/private/linear/order/create", parameters, time);
            }
            catch
            {
                SendLogMessage($"Internet Error. Order exchange error num {order.NumberUser}", LogMessageType.Error);
                order.State = OrderStateType.Fail;
                MyOrderEvent(order);
                return;
            }

            var isSuccessful = place_order_response.SelectToken("ret_msg").Value<string>();

            if (isSuccessful == "OK")
            {
                SendLogMessage($"Order num {order.NumberUser} on exchange.", LogMessageType.Trade);
                order.State = OrderStateType.Activ;

                var ordChild = place_order_response.SelectToken("result");

                order.NumberMarket = ordChild.SelectToken("order_id").ToString();

                MyOrderEvent(order);
            }
            else
            {
                SendLogMessage($"Order exchange error num {order.NumberUser}" + isSuccessful, LogMessageType.Error);
                order.State = OrderStateType.Fail;

                MyOrderEvent(order);
            }
        } // both futures

        public void CancelOrder(Order order)
        {
            Dictionary<string, string> parameters = new Dictionary<string, string>();

            parameters.Add("api_key", client.ApiKey);
            parameters.Add("symbol", order.SecurityNameCode);
            parameters.Add("order_id", order.NumberMarket);
            parameters.Add("recv_window", "90000000");

            DateTime time = GetServerTime();

            JToken cancel_order_response;
            if (futures_type == "Inverse Perpetual")
                cancel_order_response = CreatePrivatePostQuery(client, "/v2/private/order/cancel", parameters, time); ///private/linear/order/cancel
            else
                cancel_order_response = CreatePrivatePostQuery(client, "/private/linear/order/cancel", parameters, time);

            var isSuccessful = cancel_order_response.SelectToken("ret_msg").Value<string>();

            if (isSuccessful == "OK")
            {
                order.State = OrderStateType.Cancel;
                MyOrderEvent(order);
            }
            else
            {
                SendLogMessage($"Error on order cancel num {order.NumberUser}", LogMessageType.Error);
            }
        } // both

        public void GetOrdersState(List<Order> orders)
        {
            foreach (Order order in orders)
            {
                Dictionary<string, string> parameters = new Dictionary<string, string>();

                parameters.Add("api_key", client.ApiKey);
                parameters.Add("symbol", order.SecurityNameCode);
                parameters.Add("order_id", order.NumberMarket);
                parameters.Add("recv_window", "90000000");

                JToken account_response;
                if (futures_type == "Inverse Perpetual")
                    account_response = CreatePrivateGetQuery(client, "/v2/private/order", parameters); ///private/linear/order/search
                else
                    account_response = CreatePrivateGetQuery(client, "/private/linear/order/search", parameters);

                if (account_response == null)
                {
                    continue;
                }

                string isSuccessfull = account_response.SelectToken("ret_msg").Value<string>();

                if (isSuccessfull == "OK")
                {
                    if (account_response.SelectToken("result") == null)
                    {
                        continue;
                    }

                    if (account_response.SelectToken("result").SelectToken("order_status") == null)
                    {
                        continue;
                    }

                    string state = account_response.SelectToken("result").SelectToken("order_status").Value<string>();

                    switch (state)
                    {
                        case "Created":
                            order.State = OrderStateType.Activ;
                            break;
                        case "Rejected":
                            order.State = OrderStateType.Fail;
                            break;
                        case "New":
                            order.State = OrderStateType.Activ;
                            break;
                        case "PartiallyFilled":
                            order.State = OrderStateType.Patrial;
                            break;
                        case "Filled":
                            order.State = OrderStateType.Done;
                            break;
                        case "Cancelled":
                            order.State = OrderStateType.Cancel;
                            break;
                        case "PendingCancel":
                            order.State = OrderStateType.Cancel;
                            break;
                        default:
                            order.State = OrderStateType.None;
                            break;
                    }
                }
            }
        } // both

        private DateTime GetServerTime()
        {
            DateTime time = DateTime.MinValue;
            JToken t = CreatePublicGetQuery(client, "/v2/public/time");

            if (t == null)
            {
                return DateTime.UtcNow;
            }

            JToken tt = t.Root.SelectToken("time_now");

            string timeString = tt.ToString();
            time = Utils.LongToDateTime(Convert.ToInt64(timeString.ToDecimal()));
            return time;
        }

        #endregion

        #region Подписка на данные

        private void SendLoginMessage()
        {
            var login_message = BybitWsRequestBuilder.GetAuthRequest(client);
            ws_source_private.SendMessage(login_message);
        }

        List<string> _alreadySubSec = new List<string>();

        public void Subscrible(Security security)
        {
            SetPositionMode(security);

            for (int i = 0; i < _alreadySubSec.Count; i++)
            {
                if (_alreadySubSec[i] == security.Name)
                {
                    return;
                }
            }

            _alreadySubSec.Add(security.Name);

            SubscribeMarketDepth(security.Name);
            SubscribeTrades(security.Name);

            SubscribeOrders();
            SubscribeMyTrades();
        }

        private void SubscribeMarketDepth(string security)
        {
            string request = BybitWsRequestBuilder.GetSubscribeRequest("orderBookL2_25." + security);

            ws_source_public?.SendMessage(request);
        }

        private void SubscribeTrades(string security)
        {
            string request;

            request = BybitWsRequestBuilder.GetSubscribeRequest("trade." + security);

            ws_source_public?.SendMessage(request);
        }

        private bool _alreadySubscribleOrders;

        private bool _alreadySubscribleTrades;

        private void SubscribeOrders()
        {
            if (_alreadySubscribleOrders)
            {
                return;
            }
            _alreadySubscribleOrders = true;
            string request = BybitWsRequestBuilder.GetSubscribeRequest("order");
            ws_source_private?.SendMessage(request);
        }

        private void SubscribeMyTrades()
        {
            if (_alreadySubscribleTrades)
            {
                return;
            }
            _alreadySubscribleTrades = true;
            string request = BybitWsRequestBuilder.GetSubscribeRequest("execution");

            ws_source_private?.SendMessage(request);
        }

        #endregion

        #region message handlers

        private void HandleAuthMessage(JToken response)
        {
            SendLogMessage("Bybit: Successful authorization", LogMessageType.System);
        }

        private void HandlePingMessage(JToken response)
        {
            last_time_update_socket = DateTime.UtcNow;
        }

        private void HandleSubscribeMessage(JToken response)
        {
            string subscribe = response.SelectToken("request").SelectToken("args").First().Value<string>();

            SendLogMessage("Bybit: Successful subscribed to " + subscribe, LogMessageType.System);
        }

        private void HandleorderBookL2_25Message(JToken response)
        {
            List<MarketDepth> new_md_list = new List<MarketDepth>();

            if (response.SelectToken("type").Value<string>() == "snapshot")
            {
                new_md_list = market_mepth_creator.CreateNew(response.SelectToken("data"), client);
            }

            if (response.SelectToken("type").Value<string>() == "delta")
            {
                new_md_list = market_mepth_creator.Update(response.SelectToken("data"));
            }

            foreach (var depth in new_md_list)
            {
                SortAsksMarketDepth(depth);
                var time = Convert.ToInt64(response.SelectToken("timestamp_e6").ToString());
                depth.Time = TimeManager.GetDateTimeFromTimeStamp(time / 1000);
                MarketDepthEvent(depth);
            }
        }

        private void SortAsksMarketDepth(MarketDepth depths)
        {
            for (int i = depths.Asks.Count - 1; i >= 0; i--)
            {
                for (int j = 0; j < i; j++)
                {
                    if (depths.Asks[j].Price > depths.Asks[j + 1].Price)
                    {
                        var tmp = depths.Asks[j + 1];
                        depths.Asks[j + 1] = depths.Asks[j];
                        depths.Asks[j] = tmp;
                    }
                }
            }
        }

        private void HandleTradesMessage(JToken response)
        {
            List<Trade> new_trades = BybitTradesCreator.CreateFromWS(response.SelectToken("data"));

            foreach (var trade in new_trades)
            {
                NewTradesEvent(trade);
            }
        }

        private void HandleOrderMessage(JToken response)
        {
            List<Order> new_my_orders = BybitOrderCreator.Create(response.SelectToken("data"));

            foreach (var order in new_my_orders)
            {
                MyOrderEvent(order);
            }
        }

        private void HandleMyTradeMessage(JToken data)
        {
            List<MyTrade> new_my_trades = BybitTradesCreator.CreateMyTrades(data.SelectToken("data"));

            foreach (var trade in new_my_trades)
            {
                MyTradeEvent(trade);
            }
        }

        #endregion

        #region Работа со свечами

        public List<Candle> GetCandleHistory(string nameSec, TimeSpan tf)
        {
            var diff = new TimeSpan(0, (int)(tf.TotalMinutes * 200), 0);

            return GetCandles((int)tf.TotalMinutes, nameSec, DateTime.UtcNow - diff, DateTime.UtcNow);
        }

        public List<Candle> GetCandleDataToSecurity(Security security, TimeFrameBuilder time_frame_builder, DateTime start_time, DateTime end_time, DateTime actual_time)
        {
            List<Candle> candles = new List<Candle>();

            int old_interval = Convert.ToInt32(time_frame_builder.TimeFrameTimeSpan.TotalMinutes);

            candles = GetCandles(old_interval, security.Name, start_time, end_time);

            if (candles.Count == 0)
            {
                return null;
            }

            return candles;
        }

        private List<Candle> GetCandles(int old_interval, string security, DateTime start_time, DateTime end_time)
        {
            lock (locker_candles)
            {
                List<Candle> tmp_candles = new List<Candle>();
                DateTime end_over = end_time;

                string need_interval_for_query = CandlesCreator.DetermineAppropriateIntervalForRequest(old_interval, supported_intervals, out var need_interval);

                while (true)
                {
                    var from = TimeManager.GetTimeStampSecondsToDateTime(start_time);

                    if (end_over <= start_time)
                    {
                        break;
                    }

                    List<Candle> new_candles =
                        BybitCandlesCreator.GetCandleCollection(client, security, need_interval_for_query, from, this);

                    if (new_candles != null && new_candles.Count != 0)
                        tmp_candles.AddRange(new_candles);
                    else
                        break;

                    start_time = tmp_candles[tmp_candles.Count - 1].TimeStart.AddMinutes(old_interval);

                    Thread.Sleep(20);
                }

                for (int i = tmp_candles.Count - 1; i > 0; i--)
                {
                    if (tmp_candles[i].TimeStart > end_time)
                        tmp_candles.Remove(tmp_candles[i]);
                }

                if (old_interval == need_interval)
                {
                    return tmp_candles;
                }

                List<Candle> result_candles = CandlesCreator.CreateCandlesRequiredInterval(need_interval, old_interval, tmp_candles);

                return result_candles;
            }
        }
        #endregion

        #region Работа с тиками
        public List<Trade> GetTickDataToSecurity(Security security, DateTime start_time, DateTime end_time, DateTime actual_time)
        {
            List<Trade> trades = new List<Trade>();

            trades = GetTrades(security.Name, start_time, end_time);

            if (trades.Count == 0)
            {
                return null;
            }

            return trades;
        }

        private List<Trade> GetTrades(string security, DateTime start_time, DateTime end_time)
        {
            lock (locker_candles)
            {
                List<Trade> result_trades = new List<Trade>();
                DateTime end_over = end_time;

                List<Trade> point_trades = BybitTradesCreator.GetTradesCollection(client, security, 1, -1, this);
                int last_trade_id = Convert.ToInt32(point_trades.Last().Id);

                while (true)
                {
                    List<Trade> new_trades = BybitTradesCreator.GetTradesCollection(client, security, 1000, last_trade_id - 1000, this);

                    if (new_trades != null && new_trades.Count != 0)
                    {
                        last_trade_id = Convert.ToInt32(new_trades.First().Id);

                        new_trades.AddRange(result_trades);
                        result_trades = new_trades;

                        if (result_trades.First().Time <= start_time)
                            break;
                    }
                    else
                        break;

                    Thread.Sleep(20);
                }

                for (int i = result_trades.Count - 1; i > 0; i--)
                {
                    if (result_trades[i].Time > end_time)
                        result_trades.Remove(result_trades[i]);
                }

                result_trades.Reverse();

                for (int i = result_trades.Count - 1; i > 0; i--)
                {
                    if (result_trades[i].Time < start_time)
                        result_trades.Remove(result_trades[i]);
                }

                result_trades.Reverse();

                return result_trades;
            }
        }

        #endregion

        RateGate _rateGate = new RateGate(1, TimeSpan.FromMilliseconds(100));
        public JToken CreatePrivateGetQuery(Client client, string end_point, Dictionary<string, string> parameters)
        {
            //int time_factor = 1;

            //if (client.NetMode == "Main")
            //    time_factor = 0;

            _rateGate.WaitToProceed();

            try
            {
                Dictionary<string, string> sorted_params = parameters.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value);

                StringBuilder sb = new StringBuilder();

                foreach (var param in sorted_params)
                {
                    sb.Append(param.Key + $"=" + param.Value + $"&");
                }

                long nonce = Utils.GetMillisecondsFromEpochStart();

                string str_params = sb.ToString() + "timestamp=" + (nonce).ToString();

                string url = client.RestUrl + end_point + $"?" + str_params;

                Uri uri = new Uri(url + $"&sign=" + BybitSigner.CreateSignature(client, str_params));


                HttpClient httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("referer", "OsEngine");
                var res = httpClient.GetAsync(uri).Result;
                string response_msg = res.Content.ReadAsStringAsync().Result;

                if (res.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception("Failed request " + response_msg);
                }

                return JToken.Parse(response_msg);
            }
            catch
            {
                return null;
            }

        }

        public JToken CreatePublicGetQuery(Client client, string end_point)
        {
            try
            {
                _rateGate.WaitToProceed();

                string url = client.RestUrl + end_point;

                Uri uri = new Uri(url);

                var http_web_request = (HttpWebRequest)WebRequest.Create(uri);

                HttpWebResponse http_web_response = (HttpWebResponse)http_web_request.GetResponse();

                string response_msg;

                using (var stream = http_web_response.GetResponseStream())
                {
                    using (StreamReader reader = new StreamReader(stream ?? throw new InvalidOperationException()))
                    {
                        response_msg = reader.ReadToEnd();
                    }
                }

                http_web_response.Close();

                if (http_web_response.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception("Failed request " + response_msg);
                }

                return JToken.Parse(response_msg);
            }
            catch
            {
                return null;
            }

        }

        public JToken CreatePrivatePostQuery(Client client, string end_point, Dictionary<string, string> parameters, DateTime serverTime)
        {
            _rateGate.WaitToProceed();

            parameters.Add("timestamp", (Utils.GetMillisecondsFromEpochStart(serverTime.ToUniversalTime())).ToString());

            Dictionary<string, string> sorted_params = parameters.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value);

            StringBuilder sb = new StringBuilder();

            foreach (var param in sorted_params)
            {
                if (param.Value == "false" || param.Value == "true")
                    sb.Append("\"" + param.Key + "\":" + param.Value + ",");
                else
                    sb.Append("\"" + param.Key + "\":\"" + param.Value + "\",");
            }

            StringBuilder sb_signer = new StringBuilder();

            foreach (var param in sorted_params)
            {
                sb_signer.Append(param.Key + $"=" + param.Value + $"&");
            }

            string str_signer = sb_signer.ToString();

            str_signer = str_signer.Remove(str_signer.Length - 1);

            sb.Append("\"sign\":\"" + BybitSigner.CreateSignature(client, str_signer) + "\""); // api_key=bLP2z8x0sEeFHgt14S&close_on_trigger=False&order_link_id=&order_type=Limit&price=11018.00&qty=1&side=Buy&symbol=BTCUSD&time_in_force=GoodTillCancel&timestamp=1600513511844
                                                                                               // api_key=bLP2z8x0sEeFHgt14S&close_on_trigger=False&order_link_id=&order_type=Limit&price=10999.50&qty=1&side=Buy&symbol=BTCUSD&time_in_force=GoodTillCancel&timestamp=1600514673126
                                                                                               // {"api_key":"bLP2z8x0sEeFHgt14S","close_on_trigger":"False","order_link_id":"","order_type":"Limit","price":"11050.50","qty":"1","side":"Buy","symbol":"BTCUSD","time_in_force":"GoodTillCancel","timestamp":"1600515164173","sign":"fb3c69fa5d30526810a4b60fe4b8f216a3baf2c81745289ff7ddc21ab8232ccc"}


            string url = client.RestUrl + end_point;

            string str_data = "{" + sb.ToString() + "}";


            HttpClient httpClient = new HttpClient();

            StringContent content = new StringContent(str_data, Encoding.UTF8, "application/json");
            httpClient.DefaultRequestHeaders.Add("referer", "OsEngine");
            var res = httpClient.PostAsync(url, content).Result;
            string response_msg = res.Content.ReadAsStringAsync().Result;


            return JToken.Parse(response_msg);
        }


        public void SendLogMessage(string message, LogMessageType logMessageType)
        {
            LogMessageEvent(message, logMessageType);
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
}
