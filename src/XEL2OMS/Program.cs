﻿using Microsoft.SqlServer.XEvent.Linq;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace XEL2OMS
{
    using Microsoft.Azure;
    using databaseStateDictionary = Dictionary<string, SubfolderState>;
    using serverStateDictionary = Dictionary<string, Dictionary<string, SubfolderState>>;
    using StateDictionary = Dictionary<string, Dictionary<string, Dictionary<string, SubfolderState>>>;

    public static class Program
    {
        private static TraceSource s_consoleTracer = new TraceSource("OMS");
        private static int totalLogs = 0;

        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Should catch any exception")]
        private static List<SQLAuditLog> ParseXEL(QueryableXEventData events, int eventNumber, string blobName)
        {
            int count = 0;
            List<SQLAuditLog> list = new List<SQLAuditLog>();
            foreach (var currentEvent in events)
            {
                if (count >= eventNumber)
                {
                    try
                    {
                        SQLAuditLog currentLog = new SQLAuditLog(currentEvent);
                        list.Add(currentLog);
                    }
                    catch (Exception ex)
                    {
                        s_consoleTracer.TraceEvent(TraceEventType.Error, 0, "Error: Could not send event number: {0}, from blob: {1}", count, blobName, ex);
                    }
                }

                count++;
            }
            return list;
        }

        private static string GetLocalStorageFolder()
        {
            return Environment.GetEnvironmentVariable("WEBROOT_PATH") ?? "";
        }

        public static IEnumerable<List<T>> Chunk<T>(this List<T> source, int chunkSize)
        {
            int offset = 0;
            while (offset < source.Count)
            {
                yield return source.GetRange(offset, Math.Min(source.Count - offset, chunkSize));
                offset += chunkSize;
            }
        }

        private static async Task<int> SendBlobToOMS(CloudPageBlob blob, int eventNumber, OMSIngestionApi oms)
        {
            s_consoleTracer.TraceEvent(TraceEventType.Information, 0, "processing: {0}", blob.Uri);

            string fileName = Path.Combine(GetLocalStorageFolder(), Path.GetRandomFileName() + ".xel");
            try
            {
                await blob.DownloadToFileAsync(fileName, FileMode.OpenOrCreate);
                List<SQLAuditLog> list;
                using (var events = new QueryableXEventData(fileName))
                {
                    list = ParseXEL(events, eventNumber, blob.Name);
                }
                IEnumerable<List<SQLAuditLog>> chunkedList = list.Chunk(10000);
                foreach (List<SQLAuditLog> chunk in chunkedList)
                {
                    var jsonList = JsonConvert.SerializeObject(chunk);
                    await oms.SendOMSApiIngestionFile(jsonList);
                    eventNumber += list.Count;
                    totalLogs += list.Count;
                }
            }
            finally
            {
                try
                {
                    File.Delete(fileName);
                }
                catch (Exception e)
                {
                    s_consoleTracer.TraceEvent(TraceEventType.Information, 0, "Was not able to delete file: {0}. Reason: {1}", fileName, e.Message);
                }
            }
            s_consoleTracer.TraceEvent(TraceEventType.Information, 0, "Done processing: {0}", blob.Uri);
            return eventNumber;
        }

        private static void SendLogsFromSubfolder(CloudBlobDirectory subfolder, databaseStateDictionary databaseState, OMSIngestionApi oms)
        {
            int nextEvent = 0;
            int eventNumber = 0;
            int datesCompareResult = -1;
            string lastBlob = null;
            string currentDate = null;
            string subfolderName = new DirectoryInfo(subfolder.Prefix).Name;

            IEnumerable<CloudBlobDirectory> dateFolders = GetSubDirectories(subfolderName, subfolder, databaseState);
            var subfolderState = databaseState[subfolderName];
            foreach (var dateFolder in dateFolders)
            {
                currentDate = new DirectoryInfo(dateFolder.Prefix).Name;
                datesCompareResult = string.Compare(currentDate, subfolderState.Date, StringComparison.OrdinalIgnoreCase);
                //current folder is older than last state
                if (datesCompareResult < 0)
                {
                    continue;
                }

                var tasks = new List<Task<int>>();

                IEnumerable<CloudPageBlob> pageBlobs = dateFolder.ListBlobs(useFlatBlobListing: true).OfType<CloudPageBlob>()
                    .Where(b => b.Name.EndsWith(".xel", StringComparison.OrdinalIgnoreCase));

                foreach (var blob in pageBlobs)
                {
                    string blobName = new FileInfo(blob.Name).Name;

                    if (datesCompareResult == 0)
                    {
                        int blobsCompareResult = string.Compare(blobName, subfolderState.BlobName, StringComparison.OrdinalIgnoreCase);
                        //blob is older than last state
                        if (blobsCompareResult < 0)
                        {
                            continue;
                        }

                        if (blobsCompareResult == 0)
                        {
                            eventNumber = subfolderState.EventNumber;
                        }
                    }

                    tasks.Add(SendBlobToOMS(blob, eventNumber, oms));

                    lastBlob = blobName;
                    eventNumber = 0;
                }

                Task.WaitAll(tasks.ToArray());
                nextEvent = tasks.Last().Result;
            }
            subfolderState.BlobName = lastBlob;
            if (datesCompareResult >= 0)
            {
                subfolderState.Date = currentDate;
            }

            subfolderState.EventNumber = nextEvent;
        }

        private static void SendLogsFromDatabase(CloudBlobDirectory databaseDirectory, serverStateDictionary serverState, OMSIngestionApi oms)
        {
            string databaseName = new DirectoryInfo(databaseDirectory.Prefix).Name;
            IEnumerable<CloudBlobDirectory> subfolders = GetSubDirectories(databaseName, databaseDirectory, serverState);

            foreach (var subfolder in subfolders)
            {
                SendLogsFromSubfolder(subfolder, serverState[databaseName], oms);
            }
        }

        private static void SendLogsFromServer(CloudBlobDirectory serverDirectory, StateDictionary statesList, OMSIngestionApi oms)
        {
            string serverName = new DirectoryInfo(serverDirectory.Prefix).Name;
            IEnumerable<CloudBlobDirectory> databases = GetSubDirectories(serverName, serverDirectory, statesList);

            foreach (var database in databases)
            {
                SendLogsFromDatabase(database, statesList[serverName], oms);
            }
        }

        private static IEnumerable<CloudBlobDirectory> GetSubDirectories<T>(string directoryName, CloudBlobDirectory directory, IDictionary<string, T> dictionary) where T : new()
        {
            if (!dictionary.ContainsKey(directoryName))
            {
                dictionary.Add(directoryName, new T());
            }

            return directory.ListBlobs().OfType<CloudBlobDirectory>();
        }

        private static StateDictionary GetStates(string fileName)
        {
            StateDictionary statesList;
            if (!File.Exists(fileName))
            {
                statesList = new StateDictionary();
            }
            else
            {
                using (StreamReader file = File.OpenText(fileName))
                {
                    JsonSerializer serializer = new JsonSerializer();
                    statesList = (StateDictionary)serializer.Deserialize(file, typeof(StateDictionary));
                }
            }

            return statesList;
        }

        static void Main()
        {
            string connectionString = CloudConfigurationManager.GetSetting("ConnectionString");
            string containerName = "sqldbauditlogs";
            string customerId = CloudConfigurationManager.GetSetting("omsWorkspaceId");
            string sharedKey = CloudConfigurationManager.GetSetting("omsWorkspaceKey");

            CloudStorageAccount storageAccount;
            var oms = new OMSIngestionApi(s_consoleTracer, customerId, sharedKey);

            if (CloudStorageAccount.TryParse(connectionString, out storageAccount) == false)
            {
                s_consoleTracer.TraceEvent(TraceEventType.Error, 0, "Connection string can't be parsed: {0}", connectionString);
                return;
            }
            try
            {
                CloudBlobClient BlobClient = storageAccount.CreateCloudBlobClient();
                CloudBlobContainer container = BlobClient.GetContainerReference(containerName);

                var stateFileName = Path.Combine(GetLocalStorageFolder(), "states.json");

                StateDictionary statesList = GetStates(stateFileName);

                s_consoleTracer.TraceInformation("Sending logs to OMS");

                IEnumerable<CloudBlobDirectory> servers = container.ListBlobs().OfType<CloudBlobDirectory>();
                foreach (var server in servers)
                {
                    SendLogsFromServer(server, statesList, oms);
                }

                File.WriteAllText(stateFileName, JsonConvert.SerializeObject(statesList));
                s_consoleTracer.TraceInformation("{0} logs were successfully sent", totalLogs);
            }
            catch (Exception ex)
            {
                s_consoleTracer.TraceEvent(TraceEventType.Error, 0, "Error: {0}", ex);
            }
        }
    }
}