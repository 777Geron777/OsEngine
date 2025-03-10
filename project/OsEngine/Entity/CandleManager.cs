﻿/*
 * Your rights to use code governed by this license http://o-s-a.net/doc/license_simple_engine.pdf
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using OsEngine.Logging;
using OsEngine.Market;
using OsEngine.Market.Servers;
using OsEngine.Market.Servers.Binance.Futures;
using OsEngine.Market.Servers.Binance.Spot;
using OsEngine.Market.Servers.Bitfinex;
using OsEngine.Market.Servers.BitMax;
using OsEngine.Market.Servers.BitMex;
using OsEngine.Market.Servers.GateIo;
using OsEngine.Market.Servers.Kraken;
using OsEngine.Market.Servers.QuikLua;
using OsEngine.Market.Servers.Tester;
using OsEngine.Market.Servers.Transaq;
using OsEngine.Market.Servers.ZB;
using OsEngine.Market.Servers.Hitbtc;
using OsEngine.Market.Servers.Huobi.Futures;
using OsEngine.Market.Servers.Huobi.FuturesSwap;
using OsEngine.Market.Servers.Huobi.Spot;
using OsEngine.Market.Servers.Tinkoff;
using OsEngine.Market.Servers.GateIo.Futures;
using OsEngine.Market.Servers.Bybit;
using OsEngine.Market.Servers.InteractiveBrokers;
using OsEngine.Market.Servers.OKX;
using OsEngine.Market.Servers.BitMaxFutures;
using OsEngine.Market.Servers.BybitSpot;
using OsEngine.Market.Servers.BitGet.BitGetSpot;
using OsEngine.Market.Servers.BitGet.BitGetFutures;
using OsEngine.Market.Servers.Alor;
using OsEngine.Market.Servers.GateIo.GateIoSpot;
using OsEngine.Market.Servers.GateIo.GateIoFutures;

namespace OsEngine.Entity
{
    /// <summary>
    /// /// keeper of a series of candles. It is created in the server and participates in the process of subscribing to candles.
    /// Stores a series of candles, is responsible for their loading with ticks so that candles are formed in them
    /// хранитель серий свечек. Создаётся в сервере и участвует в процессе подписки на свечки. 
    /// Хранит в себе серии свечек, отвечает за их прогрузку тиками, чтобы в них формировались свечи
    /// </summary>
    public class CandleManager
    {
        /// <summary>
        /// constructor
        /// конструктор
        /// </summary>
        /// <param name="server">the server from which the candlestick data will go/сервер из которго будут идти данные для создания свечек</param>
        /// <param name="startProgram">the program that created the class object/программа которая создала объект класса</param>
        public CandleManager(IServer server, StartProgram startProgram)
        {
            _server = server;
            _server.NewTradeEvent += server_NewTradeEvent;
            _server.TimeServerChangeEvent += _server_TimeServerChangeEvent;
            _server.NewMarketDepthEvent += _server_NewMarketDepthEvent;
            _candleSeriesNeadToStart = new Queue<CandleSeries>();

            _startProgram = startProgram;

            if(startProgram != StartProgram.IsOsOptimizer)
            {
                Task task = new Task(CandleStarterThread);
                task.Start();
            }

            TypeTesterData = TesterDataType.Unknown;
        }

        StartProgram _startProgram;

        /// <summary>
        /// server time has changed. Inbound event
        /// время сервера изменилось. Входящее событие
        /// </summary>
        /// <param name="dateTime">new server time/новое время сервера</param>
        private void _server_TimeServerChangeEvent(DateTime dateTime)
        {
            try
            {
                for (int i = 0; _activSeriesBasedOnTrades != null && i < _activSeriesBasedOnTrades.Count; i++)
                {
                    _activSeriesBasedOnTrades[i].SetNewTime(dateTime);
                }

                for (int i = 0; _activSeriesBasedOnMd != null && i < _activSeriesBasedOnMd.Count; i++)
                {
                    _activSeriesBasedOnMd[i].SetNewTime(dateTime);
                }

            }
            catch (Exception error)
            {
                SendLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// A new tick appeared in the server. Inbound event
        /// в сервере появился новый тик. Входящее событие
        /// </summary>
        /// <param name="trades">новый тик</param>
        private void server_NewTradeEvent(List<Trade> trades)
        {
            if (_server.ServerType == ServerType.Tester &&
                TypeTesterData == TesterDataType.Candle)
            {
                return;
            }

            try
            {
                if (trades == null ||
                    trades.Count == 0 ||
                    trades[0] == null)
                {
                    return;
                }

                if (_activSeriesBasedOnTrades == null)
                {
                    return;
                }

                string secCode = trades[0].SecurityNameCode;

                for (int i = 0; i < _activSeriesBasedOnTrades.Count; i++)
                {
                    if(_activSeriesBasedOnTrades[i] == null ||
                        _activSeriesBasedOnTrades[i].Security == null)
                    {
                        continue;
                    }
                    if (_activSeriesBasedOnTrades[i].Security.Name == secCode)
                    {
                        _activSeriesBasedOnTrades[i].SetNewTicks(trades);
                    }
                }
            }
            catch (Exception error)
            {
                SendLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// from the server came a new glass
        /// из сервера пришол новый стакан
        /// </summary>
        /// <param name="marketDepth"></param>
        void _server_NewMarketDepthEvent(MarketDepth marketDepth)
        {
            if (_server.ServerType == ServerType.Tester &&
                TypeTesterData == TesterDataType.Candle)
            {
                return;
            }

            try
            {
                if (_activSeriesBasedOnMd == null)
                {
                    return;
                }

                for (int i = 0; i < _activSeriesBasedOnMd.Count; i++)
                {
                    if (_activSeriesBasedOnMd[i] == null ||
                        _activSeriesBasedOnMd[i].Security == null)
                    {
                        continue;
                    }

                    if (_activSeriesBasedOnMd[i].Security.Name == marketDepth.SecurityNameCode)
                    {
                        _activSeriesBasedOnMd[i].SetNewMarketDepth(marketDepth);
                    }
                }
            }
            catch (Exception error)
            {
                SendLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        public CandleSeries GetSeries(TimeFrameBuilder timeFrameBuilder, Security security)
        {
            for(int i = 0; _activSeriesBasedOnTrades != null && i < _activSeriesBasedOnTrades.Count;i++)
            {
                CandleSeries curSeries = _activSeriesBasedOnTrades[i];

                if(curSeries.Security.Name != security.Name ||
                    curSeries.Security.NameClass != security.NameClass)
                {
                    continue;
                }

                if(curSeries.TimeFrameBuilder.Specification.Equals(timeFrameBuilder.Specification) == false)
                {
                    continue;
                }

                return _activSeriesBasedOnTrades[i];
            }

            for (int i = 0; _activSeriesBasedOnMd != null && i < _activSeriesBasedOnMd.Count; i++)
            {
                CandleSeries curSeries = _activSeriesBasedOnMd[i];

                if (curSeries.Security.Name != security.Name ||
                    curSeries.Security.NameClass != security.NameClass)
                {
                    continue;
                }

                if (curSeries.TimeFrameBuilder.Specification.Equals(timeFrameBuilder.Specification) == false)
                {
                    continue;
                }

                return _activSeriesBasedOnMd[i];
            }

            return null;
        }

        /// <summary>
        /// start creating candles in a new series of candles
        /// начать создавать свечи в новой серии свечек
        /// </summary>
        /// <param name="series">CandleSeries который нужно запустить</param>
        public void StartSeries(CandleSeries series)
        {
            try
            {
                if (_server.ServerType != ServerType.Tester &&
                    _server.ServerType != ServerType.Optimizer &&
                    _server.ServerType != ServerType.Miner)
                {
                    series.СandleUpdeteEvent += series_СandleUpdeteEvent;
                }

                series.TypeTesterData = _typeTesterData;
                series.СandleFinishedEvent += series_СandleFinishedEvent;

                if (_activSeriesBasedOnTrades == null)
                {
                    _activSeriesBasedOnTrades = new List<CandleSeries>();
                }

                if (_activSeriesBasedOnMd == null)
                {
                    _activSeriesBasedOnMd = new List<CandleSeries>();
                }

                if (_startProgram == StartProgram.IsOsTrader)
                {
                    _candleSeriesNeadToStart.Enqueue(series);
                }
                else
                {
                    if (series.CandleMarketDataType == CandleMarketDataType.MarketDepth)
                    {
                        _activSeriesBasedOnMd.Add(series);
                    }
                    else if (series.CandleMarketDataType == CandleMarketDataType.Tick)
                    {
                        _activSeriesBasedOnTrades.Add(series);
                    }
                    series.IsStarted = true;
                }
            }
            catch (Exception error)
            {
                SendLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// the turn of the series of candles to be loaded
        /// очередь серий свечек которую нужно подгрузить
        /// </summary>
        private Queue<CandleSeries> _candleSeriesNeadToStart;

        /// <summary>
        /// the method in which the processing queue _candleSeriesNeadToStart is running
        /// метод, в котором работает поток обрабатывающий очередь _candleSeriesNeadToStart
        /// </summary>
        private async void CandleStarterThread()
        {
            try
            {
                while (true)
                {

                   await Task.Delay(20);

                    if (_isDisposed == true)
                    {
                        return;
                    }

                    if (MainWindow.ProccesIsWorked == false)
                    {
                        return;
                    }

                    if (_candleSeriesNeadToStart.Count != 0)
                    {
                        CandleSeries series = _candleSeriesNeadToStart.Dequeue();

                        if (series == null || series.IsStarted)
                        {
                            continue;
                        }

                        if (series.IsStarted)
                        {
                            if (series.CandleMarketDataType == CandleMarketDataType.MarketDepth)
                            {
                                if(_activSeriesBasedOnMd != null)
                                _activSeriesBasedOnMd.Add(series);
                            }
                            else if (series.CandleMarketDataType == CandleMarketDataType.Tick)
                            {
                                if (_activSeriesBasedOnTrades != null)
                                    _activSeriesBasedOnTrades.Add(series);
                            }

                            continue;
                        }

                        ServerType serverType = _server.ServerType;

                        if (serverType == ServerType.Tester)
                        {
                            series.IsStarted = true;
                        }

                        else if (serverType == ServerType.Plaza ||
                                 serverType == ServerType.QuikDde ||
                                 serverType == ServerType.AstsBridge ||
                                 serverType == ServerType.NinjaTrader ||
                                 serverType == ServerType.Lmax)
                        {
                            series.CandlesAll = null;
                            // further, we try to load candles with ticks
                            // далее, пытаемся пробуем прогрузить свечи при помощи тиков
                            List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);

                            series.PreLoad(allTrades);
                            // if there is a preloading of candles on the server and something is downloaded
                            // если на сервере есть предзагрузка свечек и что-то скачалось 
                            series.UpdateAllCandles();

                            series.IsStarted = true;
                        }

                        else if (serverType == ServerType.Alor)
                        {
                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = _server.GetLastCandleHistory(series.Security, series.TimeFrameBuilder, 500);

                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }

                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }

                        else if (serverType == ServerType.Tinkoff)
                        {
                            TinkoffServer tinkoff = (TinkoffServer)_server;

                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);

                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = tinkoff.GetCandleHistory(series.Security.NameId,
                                    series.TimeFrame);

                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.Tester ||
                                 serverType == ServerType.Optimizer ||
                                 serverType == ServerType.BitStamp
                            )
                        {
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.QuikLua)
                        {
                            QuikLuaServer luaServ = (QuikLuaServer)_server;
                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple || 
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = luaServ.GetQuikLuaTickHistory(series.Security);
                                if (allTrades != null && allTrades.Count != 0)
                                {
                                    series.PreLoad(allTrades);
                                }
                            }
                            else
                            {
                                List<Candle> candles = luaServ.GetQuikLuaCandleHistory(series.Security,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    //candles.Reverse();
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.BitMex)
                        {
                            BitMexServer bitMex = (BitMexServer)_server;
                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple || 
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = bitMex.GetBitMexCandleHistory(series.Security.Name,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.Kraken)
                        {
                            KrakenServer kraken = (KrakenServer)_server;

                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = kraken.GetCandleHistory(series.Security.Name,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.Binance)
                        {
                            BinanceServer binance = (BinanceServer)_server;
                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple || 
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = binance.GetCandleHistory(series.Security.Name,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.InteractiveBrokers)
                        {
                            InteractiveBrokersServer server = (InteractiveBrokersServer)_server;
                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = server.GetCandleHistory(series.Security.Name,
                                    series.TimeFrame);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.BinanceFutures)
                        {

                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                BinanceServerFutures binance = (BinanceServerFutures)_server;
                                List<Candle> candles = binance.GetCandleHistory(series.Security.Name,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.AscendEx_BitMax)
                        {
                            BitMaxProServer bitMax = (BitMaxProServer)_server;
                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = bitMax.GetCandleHistory(series.Security.Name,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.Bitfinex)
                        {
                            BitfinexServer bitfinex = (BitfinexServer)_server;
                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple || 
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = bitfinex.GetCandleHistory(series.Security.Name,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.Transaq)
                        {
                            TransaqServer transaq = (TransaqServer)_server;

                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);

                                if (allTrades is null)
                                {
                                    _server.GetTickDataToSecurity(series.Security.Name, series.Security.NameClass, DateTime.MinValue, DateTime.Now, DateTime.Now, false);
                                    allTrades = _server.GetAllTradesToSecurity(series.Security);
                                }

                                series.PreLoad(allTrades);
                                series.UpdateAllCandles();
                                series.IsStarted = true;
                            }
                            else
                            {
                                transaq.GetCandleHistory(series);
                            }
                        }
                        else if (serverType == ServerType.Exmo)
                        {
                            List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);

                            series.PreLoad(allTrades);
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.Zb)
                        {
                            ZbServer zbServer = (ZbServer)_server;

                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = zbServer.GetCandleHistory(series.Security.Name, series.TimeFrameSpan);

                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.GateIoSpot)
                        {
                            GateIoServerSpot gateIoServer = (GateIoServerSpot)_server;

                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = gateIoServer.GetCandleHistory(series.Security.Name, series.TimeFrameSpan);

                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.GateIoFutures)
                        {
                            GateIoServerFutures gateIoFutures = (GateIoServerFutures)_server;
                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = gateIoFutures.GetCandleHistory(series.Security.Name,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.Hitbtc)
                        {
                            HitbtcServer hitbtc = (HitbtcServer)_server;
                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = hitbtc.GetCandleHistory(series.Security.Name,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.HuobiSpot)
                        {
                            HuobiSpotServer huobiSpot = (HuobiSpotServer)_server;
                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = huobiSpot.GetCandleHistory(series.Security.Name,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.HuobiFutures)
                        {
                            HuobiFuturesServer huobi = (HuobiFuturesServer)_server;
                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = huobi.GetCandleHistory(series.Security.Name,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.HuobiFuturesSwap)
                        {
                            HuobiFuturesSwapServer huobi = (HuobiFuturesSwapServer)_server;
                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = huobi.GetCandleHistory(series.Security.Name,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }

                        else if (serverType == ServerType.Bybit)
                        {
                            BybitServer bybit = (BybitServer)_server;
                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = bybit.GetCandleHistory(series.Security.Name,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }

                        else if (serverType == ServerType.BybitSpot)
                        {
                            BybitSpotServer bybit = (BybitSpotServer)_server;
                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = bybit.GetCandleHistory(series.Security.Name,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }

                        else if (serverType == ServerType.BitGetSpot)
                        {
                            BitGetServerSpot bitGet = (BitGetServerSpot)_server;
                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = bitGet.GetCandleHistory(series.Security.Name,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }

                        else if (serverType == ServerType.BitGetFutures)
                        {
                            BitGetServerFutures bitGet = (BitGetServerFutures)_server;
                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                List<Candle> candles = bitGet.GetCandleHistory(series.Security.Name,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }

                        else if (serverType == ServerType.OKX)
                        {

                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                OkxServer okx = (OkxServer)_server;
                                List<Candle> candles = okx.GetCandleHistory(series.Security.Name,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }
                        else if (serverType == ServerType.Bitmax_AscendexFutures)
                        {
                            if (series.CandleCreateMethodType != CandleCreateMethodType.Simple ||
                                series.TimeFrameSpan.TotalMinutes < 1)
                            {
                                List<Trade> allTrades = _server.GetAllTradesToSecurity(series.Security);
                                series.PreLoad(allTrades);
                            }
                            else
                            {
                                BitMaxFuturesServer okx = (BitMaxFuturesServer)_server;
                                List<Candle> candles = okx.GetCandleHistory(series.Security.Name,
                                    series.TimeFrameSpan);
                                if (candles != null)
                                {
                                    series.CandlesAll = candles;
                                }
                            }
                            series.UpdateAllCandles();
                            series.IsStarted = true;
                        }


                        if (series.CandleMarketDataType == CandleMarketDataType.MarketDepth)
                        {
                            if (_activSeriesBasedOnMd != null)
                                _activSeriesBasedOnMd.Add(series);
                        }
                        else if (series.CandleMarketDataType == CandleMarketDataType.Tick)
                        {
                            if (_activSeriesBasedOnTrades != null)
                                _activSeriesBasedOnTrades.Add(series);
                        }

                    }
                }
            }
            catch (Exception error)
            {
                SendLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// stop loading candles by series
        /// прекратить загрузку свечек по серии
        /// </summary>
        /// <param name="series">a series of candles to stop/серия свечек которую нужно остановить</param>
        public void StopSeries(CandleSeries series)
        {
            try
            {
                if(series == null 
                    || series.UID == null)
                {
                    return;
                }

                series.СandleUpdeteEvent -= series_СandleUpdeteEvent;
                series.СandleFinishedEvent -= series_СandleFinishedEvent;

                for(int i = 0; _activSeriesBasedOnTrades != null && i < _activSeriesBasedOnTrades.Count;i++)
                {
                    CandleSeries curSeries = _activSeriesBasedOnTrades[i];

                    if(curSeries == null ||
                        curSeries.UID == null)
                    {
                        return;
                    }

                    if (curSeries.UID == series.UID)
                    {
                        if(_activSeriesBasedOnTrades != null)
                        {
                            _activSeriesBasedOnTrades.RemoveAt(i);
                        }
                        
                        break;
                    }
                }


                for (int i = 0; _activSeriesBasedOnMd != null && i < _activSeriesBasedOnMd.Count; i++)
                {
                    CandleSeries curSeries = _activSeriesBasedOnMd[i];

                    if (curSeries == null ||
                        curSeries.UID == null)
                    {
                        return;
                    }

                    if (curSeries.UID == series.UID)
                    {
                        if (_activSeriesBasedOnMd != null)
                        {
                            _activSeriesBasedOnMd.RemoveAt(i);
                        }

                        break;
                    }
                }
            }
            catch (Exception error)
            {
                //SendLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// Exchange Connection Server
        /// сервер подключения к бирже
        /// </summary>
        private IServer _server;
        // For TESTER
        // Для ТЕСТЕРА

        /// <summary>
        /// /// for the tester and Interactiv Brokers. Loading a new candle in the series
        /// для тестера и Interactiv Brokers. Подгрузка новой свечи в серии
        /// </summary>
        public void SetNewCandleInSeries(Candle candle, string nameSecurity, TimeSpan timeFrame)
        {

            for (int i = 0; _activSeriesBasedOnTrades != null && i < _activSeriesBasedOnTrades.Count; i++)
            {
                if (_activSeriesBasedOnTrades[i].Security.Name == nameSecurity && _activSeriesBasedOnTrades[i].TimeFrameSpan == timeFrame)
                {
                    _activSeriesBasedOnTrades[i].SetNewCandleInArray(candle);
                }
            }

            for (int i = 0; _activSeriesBasedOnMd != null && i < _activSeriesBasedOnMd.Count; i++)
            {
                if (_activSeriesBasedOnMd[i].Security.Name == nameSecurity && _activSeriesBasedOnMd[i].TimeFrameSpan == timeFrame)
                {
                    _activSeriesBasedOnMd[i].SetNewCandleInArray(candle);
                }
            }
        }

        /// <summary>
        /// for the tester. Clear series from old data
        /// для тестера. Очистить серии от старых данных
        /// </summary>
        public void Clear()
        {
            if (_activSeriesBasedOnTrades != null)
            {
                for (int i = 0; i < _activSeriesBasedOnTrades.Count; i++)
                {
                    _activSeriesBasedOnTrades[i].Clear();
                }
            }

            if (_activSeriesBasedOnMd != null)
            {
                for (int i = 0; i < _activSeriesBasedOnMd.Count; i++)
                {
                    _activSeriesBasedOnMd[i].Clear();
                }
            }

            if (_candleSeriesNeadToStart != null 
               && _candleSeriesNeadToStart.Count != 0)
            {
                _candleSeriesNeadToStart.Clear();
            }
        }

        public void Dispose()
        {
            _isDisposed = true;

            try
            {
                for (int i = 0; _activSeriesBasedOnTrades != null && i < _activSeriesBasedOnTrades.Count; i++)
                {
                    _activSeriesBasedOnTrades[i].Clear();
                    _activSeriesBasedOnTrades[i].Stop();
                }
                _activSeriesBasedOnTrades = null;

                for (int i = 0; _activSeriesBasedOnMd != null && i < _activSeriesBasedOnMd.Count; i++)
                {
                    _activSeriesBasedOnMd[i].Clear();
                    _activSeriesBasedOnMd[i].Stop();
                }
                _activSeriesBasedOnMd = null;

                if (_candleSeriesNeadToStart != null
                    && _candleSeriesNeadToStart.Count != 0)
                {
                    _candleSeriesNeadToStart.Clear();
                }
            }
            catch (Exception error)
            {
                MessageBox.Show(error.ToString());
            }

            if (_server != null)
            {
                _server.NewTradeEvent -= server_NewTradeEvent;
                _server.TimeServerChangeEvent -= _server_TimeServerChangeEvent;
                _server.NewMarketDepthEvent -= _server_NewMarketDepthEvent;
                _server = null;
            }
        }

        private bool _isDisposed;

        /// <summary>
        /// for the tester. Sync Received Data
        /// для тестера. Синхронизировать получаемые данные
        /// </summary>
        public void SynhSeries(List<string> nameSecurities)
        {
            if (nameSecurities == null || nameSecurities.Count == 0)
            {
                return;
            }

            List<CandleSeries> mySeries = new List<CandleSeries>();

            for (int i = 0; _activSeriesBasedOnTrades != null && i < _activSeriesBasedOnTrades.Count; i++)
            {
                if (nameSecurities.Find(nameSec => nameSec == _activSeriesBasedOnTrades[i].Security.Name) != null)
                {
                    mySeries.Add(_activSeriesBasedOnTrades[i]);
                }
            }
            _activSeriesBasedOnTrades = mySeries;

        }

        private TesterDataType _typeTesterData;
        /// <summary>
        /// .data type that tester ordered
        /// тип данных которые заказал тестер
        /// </summary>
        public TesterDataType TypeTesterData
        {
            get { return _typeTesterData; }
            set
            {
                _typeTesterData = value;
                for (int i = 0;_activSeriesBasedOnTrades != null && i < _activSeriesBasedOnTrades.Count; i++)
                {
                    _activSeriesBasedOnTrades[i].TypeTesterData = value;
                }

                for (int i = 0; _activSeriesBasedOnMd != null && i < _activSeriesBasedOnMd.Count; i++)
                {
                    _activSeriesBasedOnMd[i].TypeTesterData = value;
                }
            }
            
        }

        public int ActiveSeriesCount
        {
            get
            {
                int result = 0;

                if (_activSeriesBasedOnTrades != null)
                {
                    result += _activSeriesBasedOnTrades.Count;
                }

                if (_activSeriesBasedOnMd != null)
                {
                    result += _activSeriesBasedOnMd.Count;
                }

                return result;
            }
        }

        /// <summary>
        /// active series
        /// активные серии
        /// </summary>
        private List<CandleSeries> _activSeriesBasedOnTrades;

        private List<CandleSeries> _activSeriesBasedOnMd;

        /// <summary>
        /// candles were updated in one of the series. Inbound event
        /// в одной из серий обновились свечки. Входящее событие
        /// </summary>
        /// <param name="series">update series/серия по которой прошло обновление</param>
        void series_СandleUpdeteEvent(CandleSeries series)
        {
            if (CandleUpdateEvent != null)
            {
                CandleUpdateEvent( series);
            }
        }

        void series_СandleFinishedEvent(CandleSeries series)
        {
            if (CandleUpdateEvent != null)
            {
                CandleUpdateEvent(series);
            }
        }

        /// <summary>
        /// candle refreshed
        /// обновилась свечка
        /// </summary>
        public event Action<CandleSeries> CandleUpdateEvent;
        // Send messages to the top
        // Отправка сообщений на верх

        private void SendLogMessage(string message,LogMessageType type)
        {
            if (LogMessageEvent != null)
            {
                LogMessageEvent(message, type);
            }
            else if (type == LogMessageType.Error)
            {
                MessageBox.Show(message);
            }
        }

        public event Action<string, LogMessageType> LogMessageEvent;
    }
}
