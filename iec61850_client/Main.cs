/* 
 * This software implements a IEC61850 driver for OSHMI.
 * Copyright - 2023 - Ricardo Lastra Olsen
 * 
 * Requires libiec61850 from MZ Automation.
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 * 
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Net.Sockets;
using System.Text;
using System.Net;
using System.Globalization;
using System.IO;
using System.Linq;
using IEC61850.Client;
using IEC61850.Common;
using System.Text.Json;

namespace OSHMI_IEC61850_Client
{
    class Program
    {
        static public string Version = "OSHMI IEC61850 Client Driver Version 0.9";
        static public string HmiConfigFile = "c:\\oshmi\\conf\\hmi.ini";
        static public string OtherHmiIp = "";
        static public string ConfigFile = "c:\\oshmi\\conf\\iec61850_client.conf";
        static public string ReportIdsFilePrefix = "c:\\oshmi\\conf\\iec61850_report_ids_";
        static public int UdpPortSend = 9100; // send UPD messages to OSHMI to this port
        static public int UdpPortListen = 9101; // listen from OSHMI commands in this port
        static public int LogLevel = 1;
        public const int LogLevelNone = 0;
        public const int LogLevelNormal = 1;
        public const int LogLevelHigh = 2;
        public const int LogLevelDebug = 3;
        private static Mutex LogMutex = new Mutex();

        public class Iec61850Control
        {
            public string oshmi_cmd_tag; // tag name on OSHMI 
            public double value; // command value
            public ulong timestamp; // timestamp
            public bool useSelectWithValue; // will use select with value when kconv1=1 on point_list and control mode allows
            public Iec61850Entry iecEntry; // iec61850 object entry
        }
        public class RcbConfig
        {
            public bool enabled; // is enabled?
            public uint integrity_period; // integrity period
        }
        public class Iec61850Entry
        {
            public string path; // IEC61850 object path
            public FunctionalConstraint fc;
            public List<string> childs; // list of child objects
            public string dataSetName; // name of dataset that contains the object, if any
            public string rcbName; // name of report that contains the object, if any
            public bool subscribe; // reserved for future use
            public string oshmi_tag; // tag from OSHMI that will be updated
        }
        public class Iec61850Server
        {
            public string name; // descriptive server name
            public string application_name; // for future use with certificates
            public string certificate_file_name; // for future use with certificates
            public string certificate_password; // for future use with certificates
            public string hostname; // server hostname or IP address
            public int port; // server tcp port
            public string password; // server password
            public bool autoTag; // forward all tags regardless mentioned in confg file 
            public bool browse; // browse all IEC61850 tree
            public bool useBrcb; // use buffered reports
            public bool useUrcb; // use unbuffered reports
            public Dictionary<string, Iec61850Entry> entries; // list of iec61850 objects
            public Dictionary<string, RcbConfig> rcbConfigs; // list of RCB configs
            public Dictionary<string, byte[]> lastReportIds; // list of last seen reports to be logged to/read from disk
            public uint read_period; // read period for tags not in reports
            public uint reports_integrity_period; // integrity period for reports
            public List<string> brcb; // list of buffered reports
            public List<string> urcb; // list of unbuffered reports
            public List<string> datasets; // list of datasets
            public int brcbCount; // count of receive buffered reports
            public Queue<Iec61850Control> ControlQueue; // controls received from OSHMI queued
        }
        public class ReptParam
        {
            public Iec61850Server srv;
            public ReportControlBlock rcb;
        }

        static void Log(string str, int level = 1)
        { // log messages with timestamp prefixed
            if (LogLevel >= level)
            {
                var now = DateTime.Now;
                LogMutex.WaitOne();
                Console.WriteLine(now.ToString("o") + "|" + str);
                LogMutex.ReleaseMutex();
            }
        }

        static void Log(System.Exception e, int level = 1)
        { // log exception messages with timestamp prefixed
            Log(e.ToString(), level);
        }
        static void SendUdp(byte[] data)
        { // send udp buffer
            using (UdpClient c = new UdpClient())
            {
                c.Send(data, data.Length, "127.0.0.1", UdpPortSend);
                if (OtherHmiIp != "")
                    c.Send(data, data.Length, OtherHmiIp, UdpPortSend);
            }
        }

        static Boolean MMSTestDoubleStateFailed(MmsValue mv)
        { // test for double state inconsistent (bitstring of 2 with same values)
            return (mv.GetType() == MmsType.MMS_BIT_STRING && mv.Size() == 2 && mv.GetBit(0) == mv.GetBit(1));
        }

        static Boolean MMSGetQualityFailed(MmsValue mv)
        { // tries to find a qualifier of iec61850 (bitstring) in a mms structure  
            Boolean f = false;
            Boolean found = false;
            switch (mv.GetType())
            {
                case MmsType.MMS_STRUCTURE:
                    for (int i = 0; i < mv.Size(); i++)
                        if (mv.GetElement(i).GetType() == MmsType.MMS_BIT_STRING)
                        {
                            f = !(mv.GetElement(i).BitStringToUInt32BigEndian() == 0);
                            found = true;
                            break;
                        }
                    if (!found)
                        f = MMSGetQualityFailed(mv.GetElement(0));
                    break;
                case MmsType.MMS_BIT_STRING:
                    if (MMSTestDoubleStateFailed(mv))
                        f = true;
                    else
                        f = !(mv.BitStringToUInt32BigEndian() == 0);
                    break;
            }
            return f;
        }

        static ulong MMSGetTimestamp(MmsValue mv)
        { // tries to find a timestamp of iec61850 (utc time) in a mms structure, return number of ms UTC
            ulong t = 0;
            Boolean found = false;
            switch (mv.GetType())
            {
                case MmsType.MMS_STRUCTURE:
                    for (int i = 0; i < mv.Size(); i++)
                        if (mv.GetElement(i).GetType() == MmsType.MMS_UTC_TIME)
                        {
                            t = mv.GetElement(i).GetUtcTimeInMs();
                            found = true;
                            break;
                        }
                    if (!found)
                        t = MMSGetTimestamp(mv.GetElement(0));
                    break;
                case MmsType.MMS_UTC_TIME:
                    t = mv.GetUtcTimeInMs();
                    break;
            }
            return t;
        }

        static Double MMSGetNumericVal(MmsValue mv, out Boolean isBinary)
        { // tries to find a numeric value of iec61850 (flot, integer, unsigned) in a mms structure  
            Double v = 0;
            Boolean found = false;
            isBinary = false;
            switch (mv.GetType())
            {
                case MmsType.MMS_STRUCTURE:
                    for (int i = 0; i < mv.Size(); i++)
                    {
                        switch (mv.GetElement(i).GetType())
                        {
                            case MmsType.MMS_FLOAT:
                                v = mv.GetElement(i).ToFloat();
                                found = true;
                                break;
                            case MmsType.MMS_INTEGER:
                                v = mv.GetElement(i).ToInt64();
                                found = true;
                                break;
                            case MmsType.MMS_UNSIGNED:
                                v = mv.GetElement(i).ToUint32();
                                found = true;
                                break;
                        }
                        if (found) break;
                    }
                    if (!found)
                        v = MMSGetNumericVal(mv.GetElement(0), out isBinary);
                    break;
                case MmsType.MMS_FLOAT:
                    v = mv.ToFloat();
                    break;
                case MmsType.MMS_INTEGER:
                    v = mv.ToInt64();
                    break;
                case MmsType.MMS_UNSIGNED:
                    v = mv.ToUint32();
                    break;
                case MmsType.MMS_BOOLEAN:
                    isBinary = true;
                    v = mv.GetBoolean() ? 1 : 0;
                    break;
                case MmsType.MMS_BIT_STRING:
                    if (mv.Size() == 2)
                    { // double state
                        isBinary = true;
                        switch (mv.ToString())
                        {
                            case "00":
                            case "01":
                                v = 0;
                                break;
                            case "11":
                            case "10":
                                v = 1;
                                break;
                        }
                    }
                    else
                        v = mv.BitStringToUInt32BigEndian();
                    break;
                default:
                    break;
            }
            return v;
        }

        static Double MMSGetDoubleVal(MmsValue mv, out Boolean isBinary)
        { // tries to convert any mms value into a double
            Double v = 0;
            isBinary = false;
            switch (mv.GetType())
            {
                case MmsType.MMS_STRUCTURE:
                    v = MMSGetNumericVal(mv, out isBinary);
                    break;
                case MmsType.MMS_BIT_STRING:
                    if (mv.Size() == 2)
                    { // double state
                        isBinary = true;
                        switch (mv.ToString())
                        {
                            case "00":
                            case "01":
                                v = 0;
                                break;
                            case "11":
                            case "10":
                                v = 1;
                                break;
                        }
                    }
                    else
                        v = mv.BitStringToUInt32BigEndian();
                    break;
                case MmsType.MMS_BOOLEAN:
                    isBinary = true;
                    v = mv.GetBoolean() ? 1 : 0;
                    break;
                case MmsType.MMS_OCTET_STRING:
                    v = mv.GetOctetStringOctet(0);
                    break;
                case MmsType.MMS_FLOAT:
                    v = mv.ToFloat();
                    break;
                case MmsType.MMS_INTEGER:
                    v = mv.ToInt64();
                    break;
                case MmsType.MMS_UNSIGNED:
                    v = mv.ToUint32();
                    break;
                case MmsType.MMS_UTC_TIME:
                    v = mv.GetUtcTimeInMs();
                    break;
                case MmsType.MMS_ARRAY:
                    v = MMSGetNumericVal(mv.GetElement(0), out isBinary);
                    break;
                case MmsType.MMS_BCD:
                case MmsType.MMS_VISIBLE_STRING:
                case MmsType.MMS_GENERALIZED_TIME:
                case MmsType.MMS_BINARY_TIME:
                case MmsType.MMS_OBJ_ID:
                case MmsType.MMS_STRING:
                case MmsType.MMS_DATA_ACCESS_ERROR:
                    break;
            }
            return v;
        }
        static string getRefFc(string dataRef, out FunctionalConstraint fc)
        {
            string ret = dataRef;
            fc = FunctionalConstraint.NONE;
            for (int i = 0; i < 17; i++)
            {
                var sfc = "$" + ((FunctionalConstraint)i).ToString() + "$";
                if (dataRef.Contains(sfc))
                {
                    fc = (FunctionalConstraint)i;
                    ret = dataRef.Replace(sfc, ".");
                    break;
                }
            }
            return ret.Replace("$", ".");
        }
        static string getRefFc2(string dataRef, out FunctionalConstraint fc)
        {
            string ret = dataRef;
            fc = FunctionalConstraint.NONE;
            for (int i = 0; i < 17; i++)
            {
                var sfc = "[" + ((FunctionalConstraint)i).ToString() + "]";
                if (dataRef.Contains(sfc))
                {
                    fc = (FunctionalConstraint)i;
                    ret = dataRef.Replace(sfc, "");
                    break;
                }
            }
            return ret;
        }

        private static void reportHandler(Report report, object parameter)
        { // handle reports, forward values to OSHMI when desired
            ReptParam rp = (ReptParam)parameter;
            string log = "";

            try
            {
                if (LogLevel > LogLevelNone)
                {
                    log = rp.srv.name + " Report RCB: " + report.GetRcbReference();
                    if (report.HasSeqNum())
                        log += " SeqNumb:" + report.GetSeqNum();
                    if (report.HasSubSeqNum())
                        log += " SubSeqNumb:" + report.GetSubSeqNum();
                    log += "\n";
                }
                byte[] entryId = report.GetEntryId();
                if (entryId != null)
                {
                    if (LogLevel > LogLevelNone)
                        log += "  entryID: " + BitConverter.ToString(entryId) + " \n";
                    if (rp.srv.brcb.Contains(report.GetRcbReference()))
                    {
                        rp.srv.lastReportIds[report.GetRcbReference()] = entryId;
                        rp.srv.brcbCount++;
                    }
                }
                if (LogLevel > LogLevelNone && report.HasDataSetName())
                    log += "  data-set: " + rp.rcb.GetDataSetReference() + "\n";

                if (report.HasTimestamp() && LogLevel > LogLevelNone)
                    log += "  timestamp: " + MmsValue.MsTimeToDateTimeOffset(report.GetTimestamp()).ToString() + "\n";

                MmsValue values = report.GetDataSetValues();

                if (LogLevel > LogLevelNone)
                    log += "  report dataset contains " + values.Size() + " elements" + "\n";

                for (int k = 0; k < values.Size(); k++)
                {
                    if (report.HasReasonForInclusion())
                        if (report.GetReasonForInclusion(k) != ReasonForInclusion.REASON_NOT_INCLUDED)
                        {
                            var dr = report.GetDataReference(k);
                            if (dr == null)
                            {
                                log += "Can't get data reference for element " + k + " of report! Skipping element...\n";
                                continue;
                            }
                            var dataRef = getRefFc(dr, out FunctionalConstraint fc);

                            if (!rp.srv.autoTag && !rp.srv.entries.ContainsKey(dataRef)) continue; // when no autoTag do not forward data for tags undefined for oshmi
                            Iec61850Entry entry = new Iec61850Entry();
                            if (rp.srv.entries.ContainsKey(dataRef))
                                entry = rp.srv.entries[dataRef];
                            else
                            {  // autoTag undefined oshmi tag with server name plus 61850 path
                                entry.path = dataRef;
                                entry.oshmi_tag = rp.srv.name + ":" + dataRef;
                                entry.childs = new List<string> { };
                            }
                            entry.rcbName = report.GetRcbReference();
                            entry.dataSetName = rp.rcb.GetDataSetReference();

                            log += "\nElement " + k + " , path " + entry.path + " , OSHMI_tag " + entry.oshmi_tag + "\n";

                            if (LogLevel > LogLevelNone)
                                log += " Included for reason " + report.GetReasonForInclusion(k).ToString() + " \n";
                            string tag = entry.oshmi_tag;
                            var value = values.GetElement(k);
                            double v;
                            bool failed;
                            ulong timestamp;
                            Boolean isBinary = false;

                            if (value.GetType() == MmsType.MMS_STRUCTURE)
                            {
                                if (LogLevel > LogLevelNone)
                                {
                                    log += " Value is of complex type [";
                                    foreach (var item in entry.childs)
                                    {
                                        log += item + " ";
                                    }
                                    log += "]\n";
                                }
                                v = MMSGetNumericVal(value, out isBinary);
                                failed = MMSGetQualityFailed(value);
                                timestamp = MMSGetTimestamp(value);

                                for (int i = 0; i < value.Size(); i++)
                                {
                                    if (LogLevel > LogLevelNone)
                                        log += "  " + value.GetElement(i).GetType();

                                    if (value.GetElement(i).GetType() == MmsType.MMS_STRUCTURE)
                                    {
                                        v = MMSGetNumericVal(value.GetElement(i), out isBinary);
                                        for (int j = 0; j < value.GetElement(i).Size(); j++)
                                        {
                                            if (LogLevel > LogLevelNone)
                                                log += "  " + value.GetElement(i).GetElement(j).GetType();
                                            if (LogLevel > LogLevelNone)
                                                log += "     -> " + value.GetElement(i).GetElement(j).ToString() + "\n";
                                            v = MMSGetNumericVal(value.GetElement(i).GetElement(j), out isBinary);
                                        }
                                    }
                                    failed = MMSGetQualityFailed(value.GetElement(i));
                                    timestamp = MMSGetTimestamp(value.GetElement(i));
                                    if (value.GetElement(i).GetType() == MmsType.MMS_BIT_STRING)
                                    {
                                        if (LogLevel > LogLevelNone)
                                            log += "   -> " + value.GetElement(i).ToString() + "\n";
                                    }
                                    else
                                    if (value.GetElement(i).GetType() == MmsType.MMS_UTC_TIME)
                                    {
                                        if (LogLevel > LogLevelNone)
                                            log += "   -> " + value.GetElement(i).GetUtcTimeAsDateTimeOffset() + "\n";
                                    }
                                    else
                                    {
                                        if (LogLevel > LogLevelNone)
                                            log += "   -> " + v + "\n";
                                    }
                                }

                                string vstr;
                                if (isBinary)
                                    vstr = v != 0 ? "true" : "false";
                                else
                                    vstr = v.ToString("G", CultureInfo.CreateSpecificCulture("en-US"));

                                SendUdp(Encoding.ASCII.GetBytes(
                                     "[{\"tag\":\"" + tag +
                                     "\",\"value\":" + vstr +
                                     ",\"failed\":" + (failed ? "true" : "false") +
                                     ((report.GetReasonForInclusion(k) == ReasonForInclusion.REASON_DATA_CHANGE && timestamp != 0) ?
                                      (",\"timestamp\": " + timestamp / 1000 + ",\"ms\":" + timestamp % 1000) : "") +
                                     "}]"));
                            }
                            else
                            {
                                v = MMSGetDoubleVal(value, out isBinary);
                                if (LogLevel > LogLevelNone)
                                {
                                    log += " Value is of simple type " + value.GetType() + " " + v;
                                }
                                failed = false;
                                if (MMSTestDoubleStateFailed(value)) failed = true; // double state inconsistent state
                                string vstr;
                                if (isBinary)
                                    vstr = v != 0 ? "true" : "false";
                                else
                                    vstr = v.ToString("G", CultureInfo.CreateSpecificCulture("en-US"));

                                SendUdp(Encoding.ASCII.GetBytes(
                                 "[{\"tag\":\"" + tag +
                                 "\",\"value\":" + vstr +
                                 ",\"failed\":" + (failed ? "true" : "false") +
                                 (report.GetReasonForInclusion(k) == ReasonForInclusion.REASON_DATA_CHANGE ?
                                 (",\"timestamp\": " + (Int32)((DateTimeOffset)DateTime.Now).ToUnixTimeSeconds()) + ",\"ms\":" + 0 : "") +
                                 "}]"));
                            }
                        }
                }
                Log(log);
            }
            catch (Exception e)
            {
                Log(log);
                Log(e);
            }
        }

        static void Process(Iec61850Server srv)
        { // handle a 61850 connection with a server (ied)
            CultureInfo ci = new CultureInfo("en-US");
            Thread.CurrentThread.CurrentCulture = ci;
            Thread.CurrentThread.CurrentUICulture = ci;
            int brcbCountPrev = 0;

            do
            {
                IedConnection con = new IedConnection();
                try
                {
                    if (srv.password != "")
                    {
                        IsoConnectionParameters parameters = con.GetConnectionParameters();
                        parameters.UsePasswordAuthentication(srv.password);
                    }

                    Log("Connect to " + srv.name);
                    con.Connect(srv.hostname, srv.port);
                    MmsConnection mmsCon = con.GetMmsConnection();
                    MmsServerIdentity identity = mmsCon.GetServerIdentity();
                    Log("Vendor:   " + identity.vendorName);
                    Log("Model:    " + identity.modelName);
                    Log("Revision: " + identity.revision);

                    List<string> serverDirectory = con.GetServerDirectory(false);

                    foreach (string ldName in serverDirectory)
                    { // logical devices
                        Log(srv.name + " LD: " + ldName);
                        List<string> lnNames = con.GetLogicalDeviceDirectory(ldName);

                        foreach (string lnName in lnNames)
                        {
                            Log(srv.name + "  LN: " + lnName);
                            string logicalNodeReference = ldName + "/" + lnName;

                            // discover data objects
                            List<string> dataObjects =
                                con.GetLogicalNodeDirectory(logicalNodeReference, ACSIClass.ACSI_CLASS_DATA_OBJECT);

                            if (srv.browse)
                                foreach (string dataObject in dataObjects)
                                {
                                    Log(srv.name + "    DO: " + dataObject);
                                    List<string> dataDirectory = con.GetDataDirectoryFC(logicalNodeReference + "." + dataObject);

                                    foreach (string dataDirectoryElement in dataDirectory)
                                    {
                                        string daReference = logicalNodeReference + "." + dataObject + "." + ObjectReference.getElementName(dataDirectoryElement);

                                        // get the type specification of a variable
                                        MmsVariableSpecification specification = con.GetVariableSpecification(daReference, ObjectReference.getFC(dataDirectoryElement));

                                        Log(srv.name + "      DA/SDO: [" + ObjectReference.getFC(dataDirectoryElement) + "] " +
                                                           ObjectReference.getElementName(dataDirectoryElement) + " : " + specification.GetType()
                                                           + "(" + specification.Size() + ") ... " + daReference);
                                        if (specification.GetType() == MmsType.MMS_STRUCTURE)
                                        {
                                            foreach (MmsVariableSpecification elementSpec in specification)
                                            {
                                                Log(srv.name + "           " + elementSpec.GetName() + " : " + elementSpec.GetType() + " ... " + daReference + "." + elementSpec.GetName());
                                            }
                                        }
                                    }
                                }

                            // discover data sets
                            var dataSets =
                                con.GetLogicalNodeDirectory(logicalNodeReference, ACSIClass.ACSI_CLASS_DATA_SET);

                            foreach (var dataSet in dataSets)
                            {
                                var dataSetName = logicalNodeReference + "." + dataSet;
                                Log(srv.name + "    Dataset: " + dataSetName);
                                srv.datasets.Add(dataSet);
                                var listData = con.GetDataSetDirectory(dataSetName);
                                // for each desired dataset entry find its child elements
                                foreach (var dataName in listData)
                                {
                                    Log(srv.name + "     " + dataSetName + " -> " + dataName);
                                    var dataRef = getRefFc2(dataName, out FunctionalConstraint fc);
                                    if (srv.entries.ContainsKey(dataRef))
                                    {
                                        var entry = srv.entries[dataRef];
                                        if (entry.fc != fc)
                                            continue;
                                        entry.dataSetName = dataSetName;
                                        Log(srv.name + "       Found desired entry " + entry.path);
                                        if (entry.childs.Count == 0)
                                        {
                                            try
                                            {
                                                var sz = con.GetVariableSpecification(entry.path, entry.fc).Size();
                                                for (int j = 0; j < sz; j++)
                                                {
                                                    var cname = con.GetVariableSpecification(entry.path, entry.fc).GetElement(j).GetName();
                                                    Log(srv.name + "         Child " + cname);
                                                    entry.childs.Add(cname);
                                                }
                                            }
                                            catch { }
                                        }
                                    }
                                }

                                // var ds = con.ReadDataSetValues(dataSetName, null);
                            }

                            if (srv.useUrcb)
                            {
                                // discover unbuffered report control blocks
                                List<string> urcbs =
                                    con.GetLogicalNodeDirectory(logicalNodeReference, ACSIClass.ACSI_CLASS_URCB);
                                foreach (string urcb in urcbs)
                                {
                                    var rcbName = logicalNodeReference + ".RP." + urcb;
                                    srv.urcb.Add(rcbName);
                                    var rcb = con.GetReportControlBlock(rcbName);

                                    var reports_integrity_period = srv.reports_integrity_period;
                                    if (srv.rcbConfigs.Count > 0 && srv.rcbConfigs.ContainsKey(rcbName))
                                    {
                                        if (!srv.rcbConfigs[rcbName].enabled)
                                        {
                                            Log(srv.name + " URCB: " + rcbName + " disabled by config.");
                                            continue;
                                        }
                                        reports_integrity_period = srv.rcbConfigs[rcbName].integrity_period;
                                    }
                                    Log(srv.name + " URCB: " + rcbName);

                                    var dataSetName = rcb.GetDataSetReference();
                                    foreach (var entry in srv.entries)
                                    {
                                        if (entry.Value.dataSetName == dataSetName)
                                        {
                                            srv.entries[entry.Key].rcbName = rcbName;
                                        }
                                    }

                                    if (rcb != null && rcb.GetObjectReference() != "")
                                    {
                                        try
                                        {
                                            rcb.GetRCBValues();
                                        }
                                        catch (IedConnectionException e)
                                        {
                                            Log(srv.name + " URCB: IED GetRCB excepion - " + e.Message);
                                        }

                                        rcb.InstallReportHandler(reportHandler, new ReptParam { srv = srv, rcb = rcb });
                                        rcb.SetTrgOps(TriggerOptions.DATA_UPDATE | TriggerOptions.DATA_CHANGED | TriggerOptions.INTEGRITY);
                                        rcb.SetIntgPd(reports_integrity_period * 1000);
                                        rcb.SetOptFlds(ReportOptions.SEQ_NUM |
                                                       ReportOptions.TIME_STAMP |
                                                       ReportOptions.REASON_FOR_INCLUSION |
                                                       ReportOptions.DATA_SET |
                                                       ReportOptions.DATA_REFERENCE |
                                                       ReportOptions.CONF_REV);

                                        rcb.SetRptEna(false);
                                        rcb.SetRptEna(true);
                                        try
                                        {
                                            rcb.SetRCBValues();
                                            rcb.SetGI(true);
                                        }
                                        catch (IedConnectionException e)
                                        {
                                            Log(srv.name + " URCB: IED SetRCB exception - " + e.Message + " Code:" + e.GetErrorCode());
                                        }
                                    }
                                }
                            }

                            if (srv.useBrcb)
                            {
                                // discover buffered report control blocks
                                List<string> brcbs =
                                    con.GetLogicalNodeDirectory(logicalNodeReference, ACSIClass.ACSI_CLASS_BRCB);
                                foreach (string brcb in brcbs)
                                {
                                    var rcbName = logicalNodeReference + ".BR." + brcb;
                                    srv.brcb.Add(rcbName);
                                    var rcb = con.GetReportControlBlock(rcbName);

                                    var reports_integrity_period = srv.reports_integrity_period;
                                    if (srv.rcbConfigs.Count > 0 && srv.rcbConfigs.ContainsKey(rcbName))
                                    {
                                        if (!srv.rcbConfigs[rcbName].enabled)
                                        {
                                            Log(srv.name + " BRCB: " + rcbName + " disabled by config.");
                                            continue;
                                        }
                                        reports_integrity_period = srv.rcbConfigs[rcbName].integrity_period;
                                    }
                                    Log(srv.name + " BRCB: " + rcbName);

                                    var dataSetName = rcb.GetDataSetReference();
                                    foreach (var entry in srv.entries)
                                    {
                                        if (entry.Value.dataSetName == dataSetName)
                                        {
                                            srv.entries[entry.Key].rcbName = rcbName;
                                        }
                                    }

                                    if (rcb != null && rcb.GetObjectReference() != "")
                                    {
                                        try
                                        {
                                            rcb.GetRCBValues();
                                        }
                                        catch (IedConnectionException e)
                                        {
                                            Log(srv.name + " BRCB: IED GetRCB excepion - " + e.Message);
                                        }

                                        rcb.InstallReportHandler(reportHandler, new ReptParam { srv = srv, rcb = rcb });
                                        rcb.SetTrgOps(TriggerOptions.DATA_UPDATE | TriggerOptions.DATA_CHANGED | TriggerOptions.INTEGRITY);
                                        byte[] lastEntryId = { 0, 0, 0, 0, 0, 0, 0, 0 };
                                        if (srv.lastReportIds.ContainsKey(rcbName))
                                        {
                                            lastEntryId = srv.lastReportIds[rcbName];
                                            Log(srv.name + " BRCB: " + rcbName + " - Last seen entryId: " + BitConverter.ToString(lastEntryId));
                                        }
                                        rcb.SetEntryID(lastEntryId);
                                        rcb.SetIntgPd(reports_integrity_period * 1000);
                                        rcb.SetOptFlds(ReportOptions.SEQ_NUM |
                                                       ReportOptions.TIME_STAMP |
                                                       ReportOptions.REASON_FOR_INCLUSION |
                                                       ReportOptions.DATA_SET |
                                                       ReportOptions.DATA_REFERENCE |
                                                       ReportOptions.CONF_REV |
                                                       ReportOptions.ENTRY_ID);
                                        rcb.SetRptEna(false);
                                        rcb.SetRptEna(true);
                                        try
                                        {
                                            rcb.SetRCBValues();
                                            rcb.SetGI(true);
                                        }
                                        catch (IedConnectionException e)
                                        {
                                            Log(srv.name + " BRCB: IED SetRCB exception - " + e.Message + " Code:" + e.GetErrorCode());
                                        }
                                    }
                                }
                            }
                        }
                    }

                    do
                    {
                        // when device disconnected, will abort and keep trying to reconnect
                        if (con.GetState() != IedConnectionState.IED_STATE_CONNECTED)
                        {
                            throw new Exception(srv.name + " Connection error detected!");
                        }

                        // foreach (IEC61850_entry entry in srv.entries)
                        for (int i = 0; i < srv.entries.Count; i++)
                        {
                            var entry = srv.entries.ElementAt(i).Value;
                            string tag;
                            if (entry.oshmi_tag == "")
                                tag = entry.path;
                            else
                                tag = entry.oshmi_tag;

                            if (entry.rcbName == "" && entry.fc != FunctionalConstraint.CO) // only read elements that are not in reports
                            {
                                int err = 6;
                                for (int j = 0; j < 5 && err == 6; j++)
                                {
                                    if (srv.ControlQueue.Count > 0)
                                        CheckCommands(srv, con);

                                    Log(srv.name + " Async Reading " + entry.path + " " + entry.fc + " ind:" + (i + 1) + " try:" + (j + 1));
                                    err = 0;
                                    try
                                    {
                                        con.ReadValueAsync(entry.path, entry.fc,
                                            delegate (uint invokeId, object parameter, IedClientError err, MmsValue value)
                                            {
                                                string log = "";
                                                if (LogLevel > LogLevelNone)
                                                    log = srv.name + " READED" + " " + entry.path + " " + tag;
                                                if (err == IedClientError.IED_ERROR_OK)
                                                {
                                                    var tp = value.GetType();
                                                    double v = 0;
                                                    bool failed = false;
                                                    ulong timestamp = 0;
                                                    bool isBinary = false;

                                                    if (value.GetType() == MmsType.MMS_STRUCTURE)
                                                    {
                                                        failed = true;
                                                        if (LogLevel > LogLevelHigh) log += "\n    Value is of complex type \n";
                                                        v = MMSGetNumericVal(value, out isBinary);
                                                        failed = MMSGetQualityFailed(value);
                                                        timestamp = MMSGetTimestamp(value);

                                                        for (int i = 0; i < value.Size(); i++)
                                                        {
                                                            if (LogLevel > LogLevelHigh) log += "    element: " + value.GetElement(i).GetType();

                                                            if (value.GetElement(i).GetType() == MmsType.MMS_STRUCTURE)
                                                            {
                                                                v = MMSGetNumericVal(value.GetElement(i), out isBinary);
                                                                for (int j = 0; j < value.GetElement(i).Size(); j++)
                                                                {
                                                                    if (LogLevel > LogLevelHigh) log += "    element: " + value.GetElement(i).GetElement(j).GetType();
                                                                    if (LogLevel > LogLevelHigh) log += " -> " + value.GetElement(i).GetElement(j).ToString() + "\n";
                                                                    v = MMSGetNumericVal(value.GetElement(i).GetElement(j), out isBinary);
                                                                }
                                                            }
                                                            failed = MMSGetQualityFailed(value.GetElement(i));
                                                            timestamp = MMSGetTimestamp(value.GetElement(i));
                                                            if (value.GetElement(i).GetType() == MmsType.MMS_BIT_STRING)
                                                            {
                                                                if (LogLevel > LogLevelHigh) log += " -> " + value.GetElement(i).ToString() + "\n";
                                                            }
                                                            else
                                                            if (value.GetElement(i).GetType() == MmsType.MMS_UTC_TIME)
                                                            {
                                                                if (LogLevel > LogLevelHigh) log += " -> " + value.GetElement(i).GetUtcTimeAsDateTimeOffset() + "\n";
                                                            }
                                                            else
                                                            {
                                                                if (LogLevel > LogLevelHigh)
                                                                    log += "   -> " + v + "\n";
                                                            }
                                                        }
                                                        string vstr;
                                                        if (isBinary)
                                                            vstr = v != 0 ? "true" : "false";
                                                        else
                                                            vstr = v.ToString("G", CultureInfo.CreateSpecificCulture("en-US"));

                                                        SendUdp(Encoding.ASCII.GetBytes(
                                                             "[{\"tag\":\"" + tag +
                                                             "\",\"value\":" + vstr +
                                                             ",\"failed\":" + (false ? "true" : "false") +
                                                             // ",\"timestamp\": " + (Int32)((DateTimeOffset)DateTime.Now).ToUnixTimeSeconds() +
                                                             //",\"ms\":" + 0 +
                                                             "}]"));
                                                        if (LogLevel > LogLevelNone) log += "    v=" + v.ToString("G", CultureInfo.CreateSpecificCulture("en-US")) + " f=" + failed + " t=" + timestamp;
                                                    }
                                                    else
                                                    {
                                                        v = MMSGetDoubleVal(value, out isBinary);
                                                        if (MMSTestDoubleStateFailed(value)) failed = true; // double state inconsistent state
                                                        string vstr;
                                                        if (isBinary)
                                                            vstr = v != 0 ? "true" : "false";
                                                        else
                                                            vstr = v.ToString("G", CultureInfo.CreateSpecificCulture("en-US"));

                                                        SendUdp(Encoding.ASCII.GetBytes(
                                                         "[{\"tag\":\"" + tag +
                                                         "\",\"value\":" + vstr +
                                                         ",\"failed\":" + (failed ? "true" : "false") +
                                                         //",\"timestamp\": " + (Int32)((DateTimeOffset)DateTime.Now).ToUnixTimeSeconds() +
                                                         //",\"ms\":" + 0 +
                                                         "}]"));
                                                        if (LogLevel > LogLevelNone) log += "    v=" + v.ToString("G", CultureInfo.CreateSpecificCulture("en-US"));
                                                    }
                                                }
                                                else
                                                {
                                                    if (LogLevel > 0) log += "    Read error: " + err.ToString();
                                                }
                                                Log(log);
                                            }, null);
                                    }
                                    catch (IedConnectionException e)
                                    {
                                        err = e.GetErrorCode();
                                        if (e.GetErrorCode() == 6)
                                        {
                                            CheckCommands(srv, con);
                                            Thread.Sleep(250);
                                        }
                                        else
                                        if (LogLevel > LogLevelNormal)
                                            Log(srv.name + " Exception reading " + entry.path + " " + entry.fc + " error:" + e.GetErrorCode());
                                    }
                                }
                            }
                        }

                        for (int i = 0; i < srv.read_period * 10; i++)
                        {
                            if (srv.ControlQueue.Count > 0)
                                CheckCommands(srv, con);
                            else
                                // wait 1/10 second
                                Thread.Sleep(100);
                        }

                        if (brcbCountPrev != srv.brcbCount)
                        {
                            brcbCountPrev = srv.brcbCount;
                            File.WriteAllText(ReportIdsFilePrefix + srv.name + ".json", JsonSerializer.Serialize(srv.lastReportIds, new JsonSerializerOptions { WriteIndented = true }));
                        }

                    } while (true);

                }
                catch (Exception e)
                {
                    Log("Exception " + srv.name);
                    Log(e.Message);
                    if (con.GetState() == IedConnectionState.IED_STATE_CONNECTED)
                        con.Abort();
                    con.Dispose();
                    System.Threading.Thread.Sleep(5000);
                }
            } while (true);

        }

        public static void Main(string[] args)
        { // args: loglevel 0 to 3, browse 1 or 0
            var browse = false;
            if (args.Length > 0)
            {
                switch (args[0])
                {
                    case "0": LogLevel = LogLevelNone; break;
                    case "1": LogLevel = LogLevelNormal; break;
                    case "2": LogLevel = LogLevelHigh; break;
                    case "3": LogLevel = LogLevelDebug; break;
                }
            }
            if (args.Length > 1)
            {
                switch (args[1])
                {
                    case "0": browse = false; break;
                    case "1": browse = true; break;
                }
            }

            CultureInfo ci = new CultureInfo("en-US");
            Thread.CurrentThread.CurrentCulture = ci;
            Thread.CurrentThread.CurrentUICulture = ci;
            Log(Version);

            // read redundant hmi IP from hmi.ini
            var dict = File.ReadLines(HmiConfigFile)
               .Where(line => !string.IsNullOrWhiteSpace(line) && !line.Trim().StartsWith(";"))
               .Select(line => line.Split(new char[] { '=' }, 2, 0))
               .ToDictionary(parts => parts[0], parts => parts.Length > 1 ? parts[1] : "");
            if (dict.ContainsKey("OTHER_HMI_IP"))
                OtherHmiIp = dict["OTHER_HMI_IP"];

            // Read config
            List<Iec61850Server> servers = new List<Iec61850Server>();
            using (StreamReader file = new StreamReader(ConfigFile))
            {
                int counter = 0;
                string ln;
                int cnt_entries = -1;
                int cnt_servers = -1;

                while ((ln = file.ReadLine()) != null)
                {
                    counter++;
                    if (ln.Trim() == "")
                        continue;

                    var result = ln.Split(',');
                    if (result[0].Trim()[0] == '#' || result.Count() == 0) // comment or empty line
                        continue;
                    if (result[0].ToLower().Contains("server") && result.Count() >= 10)
                    { // new opc ua server
                        Log("NEW SERVER");
                        string an = "OSHMI", cfn = "", passwd = "";
                        if (result.Count() >= 11)
                            an = result[10].Trim();
                        if (result.Count() >= 12)
                            cfn = result[11].Trim();
                        if (result.Count() >= 13)
                            passwd = result[12].Trim();
                        cnt_entries = -1;
                        cnt_servers++;
                        Iec61850Server iecserv = new Iec61850Server
                        {
                            name = result[1].Trim(),
                            hostname = result[2].Trim(),
                            port = System.Convert.ToInt16(result[3].Trim()),
                            password = result[4].Trim(),
                            read_period = System.Convert.ToUInt32(result[5].Trim()),
                            autoTag = result[6].Trim() == "Y",
                            reports_integrity_period = System.Convert.ToUInt32(result[7].Trim()),
                            useBrcb = result[8].Trim() == "Y",
                            useUrcb = result[9].Trim() == "Y",
                            application_name = an,
                            certificate_file_name = cfn,
                            certificate_password = passwd,
                            browse = browse,
                            entries = new Dictionary<string, Iec61850Entry>(),
                            rcbConfigs = new Dictionary<string, RcbConfig>(),
                            datasets = new List<string>(),
                            brcb = new List<string>(),
                            urcb = new List<string>(),
                            lastReportIds = new Dictionary<string, byte[]>(),
                            brcbCount = 0,
                            ControlQueue = new Queue<Iec61850Control>(),
                        };
                        if (File.Exists(ReportIdsFilePrefix + iecserv.name + ".json"))
                        {
                            var jstr = File.ReadAllText(ReportIdsFilePrefix + iecserv.name + ".json");
                            iecserv.lastReportIds =
                                JsonSerializer.Deserialize<Dictionary<string, byte[]>>(jstr);
                        }
                        servers.Add(iecserv);
                    }
                    else
                    if (result[0].ToLower().Contains("rcb") && result.Count() >= 4)
                    {
                        RcbConfig rcbConfig = new RcbConfig()
                        {
                            integrity_period = System.Convert.ToUInt32(result[2].Trim()),
                            enabled = result[3].Trim() == "Y" ? true : false,
                        };
                        servers[cnt_servers].rcbConfigs[result[1].Trim()] = rcbConfig;
                    }
                    else
                    if ((result[0].ToLower().Contains("tag") || result[0].ToLower().Contains("control")) && result.Count() >= 5)
                    { // must be a tag entry
                        Log("NEW TAG");
                        cnt_entries++;
                        Enum.TryParse(result[2].Trim(), out FunctionalConstraint fc);
                        Iec61850Entry entry = new Iec61850Entry()
                        {
                            path = result[1].Trim(),
                            fc = fc,
                            subscribe = result[3].Trim() == "Y" ? true : false,
                            oshmi_tag = result[4].Trim(),
                            childs = new List<string>(),
                            dataSetName = "",
                            rcbName = "",
                        };
                        servers[cnt_servers].entries[entry.path] = entry;
                    }
                    else
                    {
                        Log("Invalid config line: " + counter);
                    }
                    Log(ln);
                }
                file.Close();
                Log($"Config file has {counter} lines.");
            }

            foreach (Iec61850Server srv in servers)
            {
                Thread t = new Thread(() => Process(srv));
                t.Start();
            }

            // handle incomming controls
            UdpClient listener = new UdpClient(UdpPortListen);
            IPEndPoint groupEP = new IPEndPoint(IPAddress.Any, UdpPortListen);
            try
            {
                while (true)
                {
                    // Waiting for messages
                    byte[] bytes = listener.Receive(ref groupEP);
                    byte[] localhost = { 127, 0, 0, 1 };
                    // allow incomming packets only from localhost or from redundant hmi
                    if (groupEP.Address.MapToIPv4() != new IPAddress(localhost) &&
                        (OtherHmiIp != "" && groupEP.Address.MapToIPv4().ToString() != OtherHmiIp)
                       )
                    {
                        Log("Received packet not allowed from " + groupEP.Address.ToString(), LogLevelHigh);
                        continue;
                    }
                    Log($"Received message from {groupEP} : ");
                    string s = Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1).Trim(); // bytes.Length-1 is to remove the final \0
                    Log(s);

                    try
                    {
                        string tag = "";
                        double dval = 0;
                        bool useSelWVal = false;
                        dynamic result = JsonSerializer.Deserialize<Dictionary<string, dynamic>>(s);
                        foreach (KeyValuePair<string, object> entry in result)
                        {
                            var key = entry.Key;
                            var value = entry.Value;
                            Log(String.Format("Command {0} : {1}", key, value));
                            if (key.ToLower() == "tag")
                                tag = value.ToString();
                            if (key.ToLower() == "value")
                                dval = Double.Parse(value.ToString());
                            if (key.ToLower() == "sbo")
                                useSelWVal = value.ToString().ToLower() == "true";
                        }
                        Iec61850Control ic = new Iec61850Control() { oshmi_cmd_tag = tag, value = dval, timestamp = (ulong)DateTime.Now.Ticks, useSelectWithValue = useSelWVal };
                        foreach (var srv in servers)
                        {
                            foreach (var entry in srv.entries)
                            {
                                // enqueue command for server when oshmi tag matches
                                if (ic.oshmi_cmd_tag == entry.Value.oshmi_tag)
                                {
                                    ic.iecEntry = srv.entries[entry.Key];
                                    srv.ControlQueue.Enqueue(ic);
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        Log("ERROR: Invalid JSON Message!");
                    }
                }
            }
            catch (SocketException e)
            {
                Log(e);
            }
            finally
            {
                listener.Close();
            }
        }
        static void CheckCommands(Iec61850Server srv, IedConnection con)
        {
            if (srv.ControlQueue.Count > 0)
            {
                var ic = srv.ControlQueue.Dequeue();
                Log(srv.name + " Control " + ic.iecEntry.path + " Value " + ic.value);

                if (ic.iecEntry.fc != FunctionalConstraint.CO)
                { // simple write
                    try
                    {
                        var mmsv = con.ReadValue(ic.iecEntry.path, ic.iecEntry.fc);
                        switch (mmsv.GetType())
                        {
                            default:
                            case MmsType.MMS_BCD:
                            case MmsType.MMS_OBJ_ID:
                            case MmsType.MMS_GENERALIZED_TIME:
                            case MmsType.MMS_STRUCTURE:
                            case MmsType.MMS_ARRAY:
                                Log(srv.name + " Writable object of unsupported type! " + ic.iecEntry.path);
                                break;
                            case MmsType.MMS_BOOLEAN:
                                con.WriteValue(ic.iecEntry.path, ic.iecEntry.fc, new MmsValue(ic.value != 0));
                                break;
                            case MmsType.MMS_UNSIGNED:
                                con.WriteValue(ic.iecEntry.path, ic.iecEntry.fc, new MmsValue((uint)ic.value));
                                break;
                            case MmsType.MMS_INTEGER:
                                con.WriteValue(ic.iecEntry.path, ic.iecEntry.fc, new MmsValue((long)ic.value));
                                break;
                            case MmsType.MMS_FLOAT:
                                con.WriteValue(ic.iecEntry.path, ic.iecEntry.fc, new MmsValue(ic.value));
                                break;
                            case MmsType.MMS_STRING:
                            case MmsType.MMS_VISIBLE_STRING:
                                con.WriteValue(ic.iecEntry.path, ic.iecEntry.fc, new MmsValue(ic.value.ToString()));
                                break;
                            case MmsType.MMS_BIT_STRING:
                                var bs = MmsValue.NewBitString(mmsv.Size());
                                bs.BitStringFromUInt32((uint)ic.value);
                                con.WriteValue(ic.iecEntry.path, ic.iecEntry.fc, bs);
                                break;
                            case MmsType.MMS_UTC_TIME:
                                var ut = MmsValue.NewUtcTime((ulong)ic.value);
                                con.WriteValue(ic.iecEntry.path, ic.iecEntry.fc, ut);
                                break;
                            case MmsType.MMS_BINARY_TIME:
                                var bt = MmsValue.NewBinaryTime(true);
                                bt.SetBinaryTime((ulong)ic.value);
                                con.WriteValue(ic.iecEntry.path, ic.iecEntry.fc, bt);
                                break;
                            case MmsType.MMS_OCTET_STRING:
                                var os = MmsValue.NewOctetString(mmsv.Size());
                                os.SetOctetStringOctet(0, (byte)(((uint)ic.value) % 256));
                                con.WriteValue(ic.iecEntry.path, ic.iecEntry.fc, os);
                                break;
                        }
                    }
                    catch (IedConnectionException ex)
                    {
                        Log(srv.name + " Writable object not found! " + ic.iecEntry.path);
                        Log(ex.Message);
                        return;
                    }
                }
                else
                { // control object
                    try
                    {
                        ControlObject control = con.CreateControlObject(ic.iecEntry.path);
                        if (control == null)
                        {
                            Log(srv.name + " Control object not found! " + ic.iecEntry.path);
                            return;
                        }
                        control.SetOrigin(Version, OrCat.STATION_CONTROL);
                        control.SetInterlockCheck(true);
                        control.SetSynchroCheck(true);
                        control.SetTestMode(false);

                        ControlModel controlModel = control.GetControlModel();
                        MmsType controlType = control.GetCtlValType();
                        Log(srv.name + " " + ic.iecEntry.path + " has control model " + controlModel.ToString());
                        Log(srv.name + "  type of ctlVal: " + controlType.ToString());

                        switch (controlModel)
                        {
                            case ControlModel.STATUS_ONLY:
                                Log(srv.name + " Control is status-only!");
                                break;
                            case ControlModel.DIRECT_NORMAL:
                            case ControlModel.DIRECT_ENHANCED:
                                switch (controlType)
                                {
                                    case MmsType.MMS_BOOLEAN:
                                        if (control.Operate(ic.value != 0))
                                        {
                                            Log(srv.name + " Operated successfully!");
                                        }
                                        else
                                        {
                                            Log(srv.name + " Operate failed!");
                                            Log(srv.name + " Error: " + control.GetLastApplError().error);
                                            Log(srv.name + " Addit.Cause: " + control.GetLastApplError().addCause);
                                        }
                                        break;
                                    case MmsType.MMS_UNSIGNED:
                                    case MmsType.MMS_INTEGER:
                                        if (control.Operate((int)ic.value))
                                        {
                                            Log(srv.name + " Operated successfully!");
                                        }
                                        else
                                        {
                                            Log(srv.name + " Operate failed!");
                                            Log(srv.name + " Error: " + control.GetLastApplError().error);
                                            Log(srv.name + " Addit.Cause: " + control.GetLastApplError().addCause);
                                        }
                                        break;
                                    case MmsType.MMS_FLOAT:
                                        if (control.Operate((float)ic.value))
                                        {
                                            Log(srv.name + " Operated successfully!");
                                        }
                                        else
                                        {
                                            Log(srv.name + " Operate failed!");
                                            Log(srv.name + " Error: " + control.GetLastApplError().error);
                                            Log(srv.name + " Addit.Cause: " + control.GetLastApplError().addCause);
                                        }
                                        break;
                                    default:
                                        Log(srv.name + " Unsupported Command Type!");
                                        break;
                                }
                                break;
                            case ControlModel.SBO_NORMAL:
                            case ControlModel.SBO_ENHANCED:
                                switch (controlType)
                                {
                                    case MmsType.MMS_BOOLEAN:
                                        if (ic.useSelectWithValue)
                                        {
                                            Log(srv.name + " Selecting with value...");
                                            if (!control.SelectWithValue(ic.value != 0))
                                            {
                                                Log(srv.name + " Select with value failed!");
                                                Log(srv.name + " Error: " + control.GetLastApplError().error);
                                                Log(srv.name + " Addit.Cause: " + control.GetLastApplError().addCause);
                                                break;
                                            }
                                        }
                                        else
                                        {
                                            Log(srv.name + " Selecting without value...");
                                            if (!control.Select())
                                            {
                                                Log(srv.name + " Select without value failed!");
                                                Log(srv.name + " Error: " + control.GetLastApplError().error);
                                                Log(srv.name + " Addit.Cause: " + control.GetLastApplError().addCause);
                                                break;
                                            }
                                        }
                                        Log(srv.name + " Selected successfully!");
                                        Thread.Sleep(100);
                                        if (control.Operate(ic.value != 0))
                                        {
                                            Log(srv.name + " Operated successfully!");
                                        }
                                        else
                                        {
                                            Log(srv.name + " Operate failed!");
                                            Log(srv.name + " Error: " + control.GetLastApplError().error);
                                            Log(srv.name + " Addit.Cause: " + control.GetLastApplError().addCause);
                                        }
                                        break;
                                    case MmsType.MMS_UNSIGNED:
                                    case MmsType.MMS_INTEGER:
                                        if (ic.useSelectWithValue)
                                        {
                                            Log(srv.name + " Selecting with value...");
                                            if (!control.SelectWithValue((int)ic.value))
                                            {
                                                Log(srv.name + " Select with value failed!");
                                                Log(srv.name + " Error: " + control.GetLastApplError().error);
                                                Log(srv.name + " Addit.Cause: " + control.GetLastApplError().addCause);
                                                break;
                                            }
                                        }
                                        else
                                        {
                                            Log(srv.name + " Selecting without value...");
                                            if (!control.Select())
                                            {
                                                Log(srv.name + " Select without value failed!");
                                                Log(srv.name + " Error: " + control.GetLastApplError().error);
                                                Log(srv.name + " Addit.Cause: " + control.GetLastApplError().addCause);
                                                break;
                                            }
                                        }
                                        Log(srv.name + " Selected successfully!");
                                        Thread.Sleep(100);
                                        if (control.Operate((int)ic.value))
                                        {
                                            Log(srv.name + " Operated successfully!");
                                        }
                                        else
                                        {
                                            Log(srv.name + " Operate failed!");
                                            Log(srv.name + " Error: " + control.GetLastApplError().error);
                                            Log(srv.name + " Addit.Cause: " + control.GetLastApplError().addCause);
                                        }
                                        break;
                                    case MmsType.MMS_FLOAT:
                                        if (ic.useSelectWithValue)
                                        {
                                            Log(srv.name + " Selecting with value...");
                                            if (!control.SelectWithValue((float)ic.value))
                                            {
                                                Log(srv.name + " Select with value failed!");
                                                Log(srv.name + " Error: " + control.GetLastApplError().error);
                                                Log(srv.name + " Addit.Cause: " + control.GetLastApplError().addCause);
                                                break;
                                            }
                                        }
                                        else
                                        {
                                            Log(srv.name + " Selecting without value...");
                                            if (!control.Select())
                                            {
                                                Log(srv.name + " Select without value failed!");
                                                Log(srv.name + " Error: " + control.GetLastApplError().error);
                                                Log(srv.name + " Addit.Cause: " + control.GetLastApplError().addCause);
                                                break;
                                            }
                                        }
                                        Log(srv.name + " Selected successfully!");
                                        Thread.Sleep(100);
                                        if (control.Operate((float)ic.value))
                                        {
                                            Log(srv.name + " Operated successfully!");
                                        }
                                        else
                                        {
                                            Log(srv.name + " Operate failed!");
                                            Log(srv.name + " Error: " + control.GetLastApplError().error);
                                            Log(srv.name + " Addit.Cause: " + control.GetLastApplError().addCause);
                                        }
                                        break;
                                    default:
                                        Log(srv.name + " Unsupported Command Type!");
                                        break;
                                }
                                break;
                        }
                        control.Dispose();
                    }
                    catch (IedConnectionException ex)
                    {
                        Log(srv.name + " Control object exception! " + ic.iecEntry.path);
                        return;
                    }
                }
            }

        }
    }
}
