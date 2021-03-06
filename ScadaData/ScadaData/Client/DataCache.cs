﻿/*
 * Copyright 2017 Mikhail Shiryaev
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 * 
 * 
 * Product  : Rapid SCADA
 * Module   : ScadaData
 * Summary  : Cache of the data received from SCADA-Server for clients usage
 * 
 * Author   : Mikhail Shiryaev
 * Created  : 2016
 * Modified : 2017
 */

using Scada.Data.Models;
using Scada.Data.Tables;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using Utils;

namespace Scada.Client
{
    /// <summary>
    /// Cache of the data received from SCADA-Server for clients usage
    /// <para>Кэш данных, полученных от SCADA-Сервера, для использования клиентами</para>
    /// </summary>
    /// <remarks>All the returned data are not thread safe
    /// <para>Все возвращаемые данные не являются потокобезопасными</para></remarks>
    public class DataCache
    {
        /// <summary>
        /// Вместимость кеша таблиц часовых срезов
        /// </summary>
        protected const int HourCacheCapacity = 100;
        /// <summary>
        /// Вместимость кеша таблиц событий
        /// </summary>
        protected const int EventCacheCapacity = 100;

        /// <summary>
        /// Период хранения таблиц часовых срезов в кеше с момента последнего доступа
        /// </summary>
        protected static readonly TimeSpan HourCacheStorePeriod = TimeSpan.FromMinutes(10);
        /// <summary>
        /// Период хранения таблиц событий в кеше с момента последнего доступа
        /// </summary>
        protected static readonly TimeSpan EventCacheStorePeriod = TimeSpan.FromMinutes(10);
        /// <summary>
        /// Время актуальности таблиц базы конфигурации
        /// </summary>
        protected static readonly TimeSpan BaseValidSpan = TimeSpan.FromSeconds(5);
        /// <summary>
        /// Время актуальности текущих и архивных данных
        /// </summary>
        protected static readonly TimeSpan DataValidSpan = TimeSpan.FromMilliseconds(500);
        /// <summary>
        /// Время ожидания снятия блокировки базы конфигурации
        /// </summary>
        protected static readonly TimeSpan WaitBaseLock = TimeSpan.FromSeconds(5);
        /// <summary>
        /// Разделитель значений внутри поля таблицы
        /// </summary>
        protected static readonly char[] FieldSeparator = new char[] { ';' };


        /// <summary>
        /// Объект для обмена данными со SCADA-Сервером
        /// </summary>
        protected readonly ServerComm serverComm;
        /// <summary>
        /// Журнал
        /// </summary>
        protected readonly Log log;

        /// <summary>
        /// Объект для синхронизации доступа к таблицам базы конфигурации
        /// </summary>
        protected readonly object baseLock;
        /// <summary>
        /// Объект для синхронизации достапа к текущим данным
        /// </summary>
        protected readonly object curDataLock;

        /// <summary>
        /// Время последего успешного обновления таблиц базы конфигурации
        /// </summary>
        protected DateTime baseRefrDT;
        /// <summary>
        /// Таблица текущего среза
        /// </summary>
        protected SrezTableLight tblCur;
        /// <summary>
        /// Время последнего успешного обновления таблицы текущего среза
        /// </summary>
        protected DateTime curDataRefrDT;


        /// <summary>
        /// Конструктор, ограничивающий создание объекта без параметров
        /// </summary>
        protected DataCache()
        {
        }

        /// <summary>
        /// Конструктор
        /// </summary>
        public DataCache(ServerComm serverComm, Log log)
        {
            if (serverComm == null)
                throw new ArgumentNullException("serverComm");
            if (log == null)
                throw new ArgumentNullException("log");

            this.serverComm = serverComm;
            this.log = log;

            baseLock = new object();
            curDataLock = new object();

            baseRefrDT = DateTime.MinValue;
            tblCur = new SrezTableLight();
            curDataRefrDT = DateTime.MinValue;

            BaseTables = new BaseTables();
            CnlProps = new InCnlProps[0];
            CtrlCnlProps = new CtrlCnlProps[0];
            CnlStatProps = new SortedList<int, CnlStatProps>();
            HourTableCache = new Cache<DateTime, SrezTableLight>(HourCacheStorePeriod, HourCacheCapacity);
            EventTableCache = new Cache<DateTime, EventTableLight>(EventCacheStorePeriod, EventCacheCapacity);
        }


        /// <summary>
        /// Получить таблицы базы конфигурации
        /// </summary>
        /// <remarks>При обновлении объект таблиц пересоздаётся, обеспечивая целостность.
        /// Таблицы после загрузки не изменяются экземпляром данного класса и не должны изменяться извне,
        /// таким образом, чтение данных из таблиц является потокобезопасным.
        /// Однако, при использовании DataTable.DefaultView небходимо синхронизировать доступ к таблицам 
        /// с помощью вызова lock (BaseTables.SyncRoot)</remarks>
        public BaseTables BaseTables { get; protected set; }

        /// <summary>
        /// Получить свойства входных каналов
        /// </summary>
        /// <remarks>Массив пересоздаётся после обновления таблиц базы конфигурации.
        /// Массив после инициализации не изменяется экземпляром данного класса и не должен изменяться извне,
        /// таким образом, чтение его данных является потокобезопасным
        /// </remarks>
        public InCnlProps[] CnlProps { get; protected set; }

        /// <summary>
        /// Получить свойства входных каналов
        /// </summary>
        /// <remarks>Массив пересоздаётся после обновления таблиц базы конфигурации.
        /// Массив после инициализации не изменяется экземпляром данного класса и не должен изменяться извне,
        /// таким образом, чтение его данных является потокобезопасным
        /// </remarks>
        public CtrlCnlProps[] CtrlCnlProps { get; protected set; }

        /// <summary>
        /// Получить свойства статусов входных каналов
        /// </summary>
        /// <remarks>Список пересоздаётся после обновления таблиц базы конфигурации.
        /// Список после инициализации не изменяется экземпляром данного класса и не должен изменяться извне,
        /// таким образом, чтение его данных является потокобезопасным
        /// </remarks>
        public SortedList<int, CnlStatProps> CnlStatProps { get; protected set; }

        /// <summary>
        /// Получить кеш таблиц часовых срезов
        /// </summary>
        /// <remarks>Использовать вне данного класса только для получения состояния кеша</remarks>
        public Cache<DateTime, SrezTableLight> HourTableCache { get; protected set; }

        /// <summary>
        /// Получить кеш таблиц событий
        /// </summary>
        /// <remarks>Использовать вне данного класса только для получения состояния кеша</remarks>
        public Cache<DateTime, EventTableLight> EventTableCache { get; protected set; }


        /// <summary>
        /// Заполнить свойства входных каналов
        /// </summary>
        protected void FillCnlProps()
        {
            try
            {
                log.WriteAction(Localization.UseRussian ?
                    "Заполнение свойств входных каналов" :
                    "Fill input channels properties");

                DataTable tblInCnl = BaseTables.InCnlTable;
                DataView viewObj = BaseTables.ObjTable.DefaultView;
                DataView viewKP = BaseTables.KPTable.DefaultView;
                DataView viewParam = BaseTables.ParamTable.DefaultView;
                DataView viewFormat = BaseTables.FormatTable.DefaultView;
                DataView viewUnit = BaseTables.UnitTable.DefaultView;

                // установка сортировки для последующего поиска строк
                viewObj.Sort = "ObjNum";
                viewKP.Sort = "KPNum";
                viewParam.Sort = "ParamID";
                viewFormat.Sort = "FormatID";
                viewUnit.Sort = "UnitID";

                int inCnlCnt = tblInCnl.Rows.Count; // количество входных каналов
                InCnlProps[] newCnlProps = new InCnlProps[inCnlCnt];

                for (int i = 0; i < inCnlCnt; i++)
                {
                    DataRow inCnlRow = tblInCnl.Rows[i];
                    InCnlProps cnlProps = new InCnlProps();

                    // определение свойств, не использующих внешних ключей
                    cnlProps.CnlNum = (int)inCnlRow["CnlNum"];
                    cnlProps.CnlName = (string)inCnlRow["Name"];
                    cnlProps.CnlTypeID = (int)inCnlRow["CnlTypeID"];
                    cnlProps.ObjNum = (int)inCnlRow["ObjNum"];
                    cnlProps.KPNum = (int)inCnlRow["KPNum"];
                    cnlProps.Signal = (int)inCnlRow["Signal"];
                    cnlProps.FormulaUsed = (bool)inCnlRow["FormulaUsed"];
                    cnlProps.Formula = (string)inCnlRow["Formula"];
                    cnlProps.Averaging = (bool)inCnlRow["Averaging"];
                    cnlProps.ParamID = (int)inCnlRow["ParamID"];
                    cnlProps.UnitID = (int)inCnlRow["UnitID"];
                    cnlProps.CtrlCnlNum = (int)inCnlRow["CtrlCnlNum"];
                    cnlProps.EvEnabled = (bool)inCnlRow["EvEnabled"];
                    cnlProps.EvSound = (bool)inCnlRow["EvSound"];
                    cnlProps.EvOnChange = (bool)inCnlRow["EvOnChange"];
                    cnlProps.EvOnUndef = (bool)inCnlRow["EvOnUndef"];
                    cnlProps.LimLowCrash = (double)inCnlRow["LimLowCrash"];
                    cnlProps.LimLow = (double)inCnlRow["LimLow"];
                    cnlProps.LimHigh = (double)inCnlRow["LimHigh"];
                    cnlProps.LimHighCrash = (double)inCnlRow["LimHighCrash"];

                    // определение наименования объекта
                    int objRowInd = viewObj.Find(cnlProps.ObjNum);
                    if (objRowInd >= 0)
                        cnlProps.ObjName = (string)viewObj[objRowInd]["Name"];

                    // определение наименования КП
                    int kpRowInd = viewKP.Find(cnlProps.KPNum);
                    if (kpRowInd >= 0)
                        cnlProps.KPName = (string)viewKP[kpRowInd]["Name"];

                    // определение наименования параметра и имени файла значка
                    int paramRowInd = viewParam.Find(cnlProps.ParamID);
                    if (paramRowInd >= 0)
                    {
                        DataRowView paramRowView = viewParam[paramRowInd];
                        cnlProps.ParamName = (string)paramRowView["Name"];
                        cnlProps.IconFileName = (string)paramRowView["IconFileName"];
                    }

                    // определение формата вывода
                    int formatRowInd = viewFormat.Find(inCnlRow["FormatID"]);
                    if (formatRowInd >= 0)
                    {
                        DataRowView formatRowView = viewFormat[formatRowInd];
                        cnlProps.ShowNumber = (bool)formatRowView["ShowNumber"];
                        cnlProps.DecDigits = (int)formatRowView["DecDigits"];
                    }

                    // определение размерностей
                    int unitRowInd = viewUnit.Find(cnlProps.UnitID);
                    if (unitRowInd >= 0)
                    {
                        DataRowView unitRowView = viewUnit[unitRowInd];
                        cnlProps.UnitName = (string)unitRowView["Name"];
                        cnlProps.UnitSign = (string)unitRowView["Sign"];
                        string[] unitArr = cnlProps.UnitArr = 
                            cnlProps.UnitSign.Split(FieldSeparator, StringSplitOptions.None);
                        for (int j = 0; j < unitArr.Length; j++)
                            unitArr[j] = unitArr[j].Trim();
                        if (unitArr.Length == 1 && unitArr[0] == "")
                            cnlProps.UnitArr = null;
                    }

                    newCnlProps[i] = cnlProps;
                }

                CnlProps = newCnlProps;
            }
            catch (Exception ex)
            {
                log.WriteException(ex, (Localization.UseRussian ? 
                    "Ошибка при заполнении свойств входных каналов: " :
                    "Error filling input channels properties"));
            }
        }

        /// <summary>
        /// Заполнить свойства каналов управления
        /// </summary>
        protected void FillCtrlCnlProps()
        {
            try
            {
                log.WriteAction(Localization.UseRussian ?
                    "Заполнение свойств каналов управления" :
                    "Fill output channels properties");

                DataTable tblCtrlCnl = BaseTables.CtrlCnlTable;
                DataView viewObj = BaseTables.ObjTable.DefaultView;
                DataView viewKP = BaseTables.KPTable.DefaultView;
                DataView viewCmdVal = BaseTables.CmdValTable.DefaultView;

                // установка сортировки для последующего поиска строк
                viewObj.Sort = "ObjNum";
                viewKP.Sort = "KPNum";
                viewCmdVal.Sort = "CmdValID";

                int ctrlCnlCnt = tblCtrlCnl.Rows.Count;
                CtrlCnlProps[] newCtrlCnlProps = new CtrlCnlProps[ctrlCnlCnt];

                for (int i = 0; i < ctrlCnlCnt; i++)
                {
                    DataRow ctrlCnlRow = tblCtrlCnl.Rows[i];
                    CtrlCnlProps ctrlCnlProps = new CtrlCnlProps();

                    // определение свойств, не использующих внешних ключей
                    ctrlCnlProps.CtrlCnlNum = (int)ctrlCnlRow["CtrlCnlNum"];
                    ctrlCnlProps.CtrlCnlName = (string)ctrlCnlRow["Name"];
                    ctrlCnlProps.CmdTypeID = (int)ctrlCnlRow["CmdTypeID"];
                    ctrlCnlProps.ObjNum = (int)ctrlCnlRow["ObjNum"];
                    ctrlCnlProps.KPNum = (int)ctrlCnlRow["KPNum"];
                    ctrlCnlProps.CmdNum = (int)ctrlCnlRow["CmdNum"];
                    ctrlCnlProps.CmdValID = (int)ctrlCnlRow["CmdValID"];
                    ctrlCnlProps.FormulaUsed = (bool)ctrlCnlRow["FormulaUsed"];
                    ctrlCnlProps.Formula = (string)ctrlCnlRow["Formula"];
                    ctrlCnlProps.EvEnabled = (bool)ctrlCnlRow["EvEnabled"];

                    // определение наименования объекта
                    int objRowInd = viewObj.Find(ctrlCnlProps.ObjNum);
                    if (objRowInd >= 0)
                        ctrlCnlProps.ObjName = (string)viewObj[objRowInd]["Name"];

                    // определение наименования КП
                    int kpRowInd = viewKP.Find(ctrlCnlProps.KPNum);
                    if (kpRowInd >= 0)
                        ctrlCnlProps.KPName = (string)viewKP[kpRowInd]["Name"];

                    // определение значений команды
                    int cmdValInd = viewCmdVal.Find(ctrlCnlProps.CmdValID);
                    if (cmdValInd >= 0)
                    {
                        DataRowView cmdValRowView = viewCmdVal[cmdValInd];
                        ctrlCnlProps.CmdValName = (string)cmdValRowView["Name"];
                        ctrlCnlProps.CmdVal = (string)cmdValRowView["Val"];
                        string[] cmdValArr = ctrlCnlProps.CmdValArr = 
                            ctrlCnlProps.CmdVal.Split(FieldSeparator, StringSplitOptions.None);
                        for (int j = 0; j < cmdValArr.Length; j++)
                            cmdValArr[j] = cmdValArr[j].Trim();
                        if (cmdValArr.Length == 1 && cmdValArr[0] == "")
                            ctrlCnlProps.CmdValArr = null;
                    }

                    newCtrlCnlProps[i] = ctrlCnlProps;
                }

                CtrlCnlProps = newCtrlCnlProps;
            }
            catch (Exception ex)
            {
                log.WriteException(ex, (Localization.UseRussian ?
                    "Ошибка при заполнении свойств каналов управления: " :
                    "Error filling output channels properties"));
            }
        }

        /// <summary>
        /// Заполнить свойства статусов входных каналов
        /// </summary>
        protected void FillCnlStatProps()
        {
            try
            {
                log.WriteAction(Localization.UseRussian ?
                    "Заполнение свойств статусов входных каналов" :
                    "Fill input channel statuses properties");

                DataTable tblEvType = BaseTables.EvTypeTable;
                int statusCnt = tblEvType.Rows.Count;
                SortedList<int, CnlStatProps> newCnlStatProps = new SortedList<int, CnlStatProps>(statusCnt);

                for (int i = 0; i < statusCnt; i++)
                {
                    DataRow row = tblEvType.Rows[i];
                    CnlStatProps cnlStatProps = new CnlStatProps((int)row["CnlStatus"]) {
                        Color = (string)row["Color"], Name = (string)row["Name"] };
                    newCnlStatProps.Add(cnlStatProps.Status, cnlStatProps);
                }

                CnlStatProps = newCnlStatProps;
            }
            catch (Exception ex)
            {
                log.WriteException(ex, (Localization.UseRussian ?
                    "Ошибка при заполнении свойств статусов входных каналов: " :
                    "Error filling input channel statuses properties"));
            }
        }

        /// <summary>
        /// Обновить текущие данные
        /// </summary>
        protected void RefreshCurData()
        {
            try
            {
                DateTime utcNowDT = DateTime.UtcNow;
                if (utcNowDT - curDataRefrDT > DataValidSpan) // данные устарели
                {
                    curDataRefrDT = utcNowDT;
                    DateTime newCurTableAge = serverComm.ReceiveFileAge(ServerComm.Dirs.Cur, SrezAdapter.CurTableName);

                    if (newCurTableAge == DateTime.MinValue) // файл среза не существует или нет связи с сервером
                    {
                        tblCur.Clear();
                        tblCur.FileModTime = DateTime.MinValue;
                        log.WriteError(Localization.UseRussian ?
                            "Не удалось принять время изменения файла текущих данных." :
                            "Unable to receive the current data file modification time.");
                    }
                    else if (tblCur.FileModTime != newCurTableAge) // файл среза изменён
                    {
                        if (serverComm.ReceiveSrezTable(SrezAdapter.CurTableName, tblCur))
                        {
                            tblCur.FileModTime = newCurTableAge;
                            tblCur.LastFillTime = utcNowDT;
                        }
                        else
                        {
                            tblCur.FileModTime = DateTime.MinValue;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                tblCur.FileModTime = DateTime.MinValue;
                log.WriteException(ex, Localization.UseRussian ?
                    "Ошибка при обновлении текущих данных" :
                    "Error refreshing the current data");
            }
        }


        /// <summary>
        /// Обновить таблицы базы конфигурации, свойства каналов и статусов
        /// </summary>
        public void RefreshBaseTables()
        {
            lock (baseLock)
            {
                try
                {
                    DateTime utcNowDT = DateTime.UtcNow;

                    if (utcNowDT - baseRefrDT > BaseValidSpan) // данные устарели
                    {
                        baseRefrDT = utcNowDT;
                        DateTime newBaseAge = serverComm.ReceiveFileAge(ServerComm.Dirs.BaseDAT,
                            BaseTables.GetFileName(BaseTables.InCnlTable));

                        if (newBaseAge == DateTime.MinValue) // база конфигурации не существует или нет связи с сервером
                        {
                            throw new ScadaException(Localization.UseRussian ?
                                "Не удалось принять время изменения базы конфигурации." :
                                "Unable to receive the configuration database modification time.");
                        }
                        else if (BaseTables.BaseAge != newBaseAge) // база конфигурации изменена
                        {
                            log.WriteAction(Localization.UseRussian ? 
                                "Обновление таблиц базы конфигурации" :
                                "Refresh the tables of the configuration database");

                            // ожидание снятия возможной блокировки базы конфигурации
                            DateTime t0 = utcNowDT;
                            while (serverComm.ReceiveFileAge(ServerComm.Dirs.BaseDAT, "baselock") > DateTime.MinValue &&
                                DateTime.UtcNow - t0 <= WaitBaseLock)
                            {
                                Thread.Sleep(ScadaUtils.ThreadDelay);
                            }

                            // загрузка данных в таблицы
                            BaseTables newBaseTables = new BaseTables() { BaseAge = newBaseAge };
                            foreach (DataTable dataTable in newBaseTables.AllTables)
                            {
                                string tableName = BaseTables.GetFileName(dataTable);

                                if (!serverComm.ReceiveBaseTable(tableName, dataTable))
                                {
                                    throw new ScadaException(string.Format(Localization.UseRussian ?
                                        "Не удалось принять таблицу {0}" :
                                        "Unable to receive the table {0}", tableName));
                                }
                            }
                            BaseTables = newBaseTables;

                            // заполнение свойств каналов и статусов
                            lock (BaseTables.SyncRoot)
                            {
                                FillCnlProps();
                                FillCtrlCnlProps();
                                FillCnlStatProps();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    BaseTables.BaseAge = DateTime.MinValue;
                    log.WriteException(ex, Localization.UseRussian ?
                        "Ошибка при обновлении таблиц базы конфигурации" :
                        "Error refreshing the tables of the configuration database");
                }
            }
        }

        /// <summary>
        /// Получить текущий срез из кеша или от сервера
        /// </summary>
        /// <remarks>Возвращаемый срез после загрузки не изменяется экземпляром данного класса,
        /// таким образом, чтение его данных является потокобезопасным</remarks>
        public SrezTableLight.Srez GetCurSnapshot(out DateTime dataAge)
        {
            lock (curDataLock)
            {
                try
                {
                    RefreshCurData();
                    dataAge = tblCur.FileModTime;
                    return tblCur.SrezList.Count > 0 ? tblCur.SrezList.Values[0] : null;
                }
                catch (Exception ex)
                {
                    log.WriteException(ex, Localization.UseRussian ?
                        "Ошибка при получении текущего среза из кеша или от сервера" :
                        "Error getting the current snapshot the cache or from the server");
                    dataAge = DateTime.MinValue;
                    return null;
                }
            }
        }

        /// <summary>
        /// Получить таблицу часовых данных за сутки из кеша или от сервера
        /// </summary>
        /// <remarks>Возвращаемая таблица после загрузки не изменяется экземпляром данного класса,
        /// таким образом, чтение её данных является потокобезопасным. 
        /// Метод всегда возвращает объект, не равный null</remarks>
        public SrezTableLight GetHourTable(DateTime date)
        {
            try
            {
                // получение таблицы часовых срезов из кеша
                date = date.Date;
                DateTime utcNowDT = DateTime.UtcNow;
                Cache<DateTime, SrezTableLight>.CacheItem  cacheItem = HourTableCache.GetOrCreateItem(date, utcNowDT);

                // блокировка доступа только к одной таблице часовых срезов
                lock (cacheItem)
                {
                    SrezTableLight table = cacheItem.Value; // таблица, которую необходимо получить
                    DateTime tableAge = cacheItem.ValueAge; // время изменения файла таблицы
                    bool tableIsNotValid = utcNowDT - cacheItem.ValueRefrDT > DataValidSpan; // таблица могла устареть

                    // получение таблицы часовых срезов от сервера
                    if (table == null || tableIsNotValid)
                    {
                        string tableName = SrezAdapter.BuildHourTableName(date);
                        DateTime newTableAge = serverComm.ReceiveFileAge(ServerComm.Dirs.Hour, tableName);

                        if (newTableAge == DateTime.MinValue) // файл таблицы не существует или нет связи с сервером
                        {
                            table = null;
                            // не засорять лог
                            /*log.WriteError(string.Format(Localization.UseRussian ?
                                "Не удалось принять время изменения таблицы часовых данных {0}" :
                                "Unable to receive modification time of the hourly data table {0}", tableName));*/
                        }
                        else if (newTableAge != tableAge) // файл таблицы изменён
                        {
                            table = new SrezTableLight();
                            if (serverComm.ReceiveSrezTable(tableName, table))
                            {
                                table.FileModTime = newTableAge;
                                table.LastFillTime = utcNowDT;
                            }
                            else
                            {
                                throw new ScadaException(Localization.UseRussian ?
                                    "Не удалось принять таблицу часовых срезов." :
                                    "Unable to receive hourly data table.");
                            }
                        }

                        if (table == null)
                            table = new SrezTableLight();

                        // обновление таблицы в кеше
                        HourTableCache.UpdateItem(cacheItem, table, newTableAge, utcNowDT);
                    }

                    return table;
                }
            }
            catch (Exception ex)
            {
                log.WriteException(ex, Localization.UseRussian ?
                    "Ошибка при получении таблицы часовых данных за {0} из кэша или от сервера" :
                    "Error getting hourly data table for {0} from the cache or from the server", 
                    date.ToLocalizedDateString());
                return new SrezTableLight();
            }
        }

        /// <summary>
        /// Получить таблицу событий за сутки из кеша или от сервера
        /// </summary>
        /// <remarks>Возвращаемая таблица после загрузки не изменяется экземпляром данного класса,
        /// таким образом, чтение её данных является потокобезопасным.
        /// Метод всегда возвращает объект, не равный null</remarks>
        public EventTableLight GetEventTable(DateTime date)
        {
            try
            {
                // получение таблицы событий из кеша
                date = date.Date;
                DateTime utcNowDT = DateTime.UtcNow;
                Cache<DateTime, EventTableLight>.CacheItem cacheItem = EventTableCache.GetOrCreateItem(date, utcNowDT);

                // блокировка доступа только к одной таблице событий
                lock (cacheItem)
                {
                    EventTableLight table = cacheItem.Value; // таблица, которую необходимо получить
                    DateTime tableAge = cacheItem.ValueAge;  // время изменения файла таблицы
                    bool tableIsNotValid = utcNowDT - cacheItem.ValueRefrDT > DataValidSpan; // таблица могла устареть

                    // получение таблицы событий от сервера
                    if (table == null || tableIsNotValid)
                    {
                        string tableName = EventAdapter.BuildEvTableName(date);
                        DateTime newTableAge = serverComm.ReceiveFileAge(ServerComm.Dirs.Events, tableName);

                        if (newTableAge == DateTime.MinValue) // файл таблицы не существует или нет связи с сервером
                        {
                            table = null;
                            // не засорять лог
                            /*log.WriteError(string.Format(Localization.UseRussian ?
                                "Не удалось принять время изменения таблицы событий {0}" :
                                "Unable to receive modification time of the event table {0}", tableName));*/
                        }
                        else if (newTableAge != tableAge) // файл таблицы изменён
                        {
                            table = new EventTableLight();
                            if (serverComm.ReceiveEventTable(tableName, table))
                            {
                                table.FileModTime = newTableAge;
                                table.LastFillTime = utcNowDT;
                            }
                            else
                            {
                                throw new ScadaException(Localization.UseRussian ?
                                    "Не удалось принять таблицу событий." :
                                    "Unable to receive event table.");
                            }
                        }

                        if (table == null)
                            table = new EventTableLight();

                        // обновление таблицы в кеше
                        EventTableCache.UpdateItem(cacheItem, table, newTableAge, utcNowDT);
                    }

                    return table;
                }
            }
            catch (Exception ex)
            {
                log.WriteException(ex, Localization.UseRussian ?
                    "Ошибка при получении таблицы событий за {0} из кэша или от сервера" :
                    "Error getting event table for {0} from the cache or from the server",
                    date.ToLocalizedDateString());
                return new EventTableLight();
            }
        }

        /// <summary>
        /// Получить тренд минутных данных заданного канала за сутки
        /// </summary>
        /// <remarks>Возвращаемый тренд после загрузки не изменяется экземпляром данного класса,
        /// таким образом, чтение его данных является потокобезопасным.
        /// Метод всегда возвращает объект, не равный null</remarks>
        public Trend GetMinTrend(DateTime date, int cnlNum)
        {
            Trend trend = new Trend(cnlNum);

            try
            {
                if (serverComm.ReceiveTrend(SrezAdapter.BuildMinTableName(date), date, trend))
                {
                    trend.LastFillTime = DateTime.UtcNow; // единообразно с часовыми данными и событиями
                }
                else
                {
                    throw new ScadaException(Localization.UseRussian ?
                        "Не удалось принять тренд." :
                        "Unable to receive trend.");
                }
            }
            catch (Exception ex)
            {
                log.WriteException(ex, Localization.UseRussian ?
                    "Ошибка при получении тренда минутных данных за {0}" :
                    "Error getting minute data trend for {0}", date.ToLocalizedDateString());
            }

            return trend;
        }
    }
}