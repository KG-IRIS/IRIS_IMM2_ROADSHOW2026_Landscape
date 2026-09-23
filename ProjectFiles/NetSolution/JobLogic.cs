#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.WebUI;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.CoreBase;
using FTOptix.Alarm;
using FTOptix.Recipe;
using FTOptix.EventLogger;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.Retentivity;
using FTOptix.MQTTClient;
using FTOptix.DataLogger;
using FTOptix.Core;
using FTOptix.RAEtherNetIP;
using FTOptix.OPCUAServer;
#endregion

public class JobLogic : BaseNetLogic
{
    private const string DatasourceURL = "https://kendall-disc.test.on.plex.com";
    private const string DataSourceId = "10638";
    private const string BasicAuthHeader = "Basic SXJpc0lBRGlzY3JldGVXc0BwbGV4LmNvbTphMDc5NjkyLWIzMTctNA==";
    private const string ConnectURL = "https://test.connect.plex.com";

    private PeriodicTask _periodicTask;

    public override void Start()
    {
        _periodicTask = new PeriodicTask(FetchAndDisplay, 4000, LogicObject);
        _periodicTask.Start();
    }

    public override void Stop()
    {
        _periodicTask?.Dispose();
    }

    private void FetchAndDisplay()
    {
        try
        {
            int workcenterKey = GetWorkcenterKey();
            if (workcenterKey < 0) return;

            string workcenterLabel = GetWorkcenterLabel();

            var row = FetchJobRow(workcenterKey, workcenterLabel);
            var scheduleJobs = FetchSchedulingJobs();
            var sched = MatchSchedule(row, scheduleJobs);

            PopulateModelVariables(row, sched);
        }
        catch (Exception ex)
        {
            Log.Error("JobLogic", $"FetchAndDisplay failed: {ex.Message}");
        }
    }

    private void PopulateModelVariables((List<string> columns, JArray row)? data, SchedulingJobDto schedule)
    {
        try
        {
            var folder = GetFolder();
            if (folder == null) return;

            if (data == null)
            {
                SetModelVar(folder, "Name", "—");
                SetModelVar(folder, "Quantity", "—");
                SetModelVar(folder, "Due Date", "—");
                SetModelVar(folder, "Job Type", "—");
                SetModelVar(folder, "Job Status", "—");
                SetModelVar(folder, "Priority", "—");
                SetModelVar(folder, "Quantity Completed", "—");
                return;
            }

            var columns = data.Value.columns;
            var row = data.Value.row;

            SetModelVar(folder, "Name", GetCellValue(columns, row, "Job_No"));
            SetModelVar(folder, "Quantity", GetCellValue(columns, row, "Job_Quantity"));
            SetModelVar(folder, "Job Type", GetCellValue(columns, row, "Job_Type"));
            SetModelVar(folder, "Quantity Completed", GetCellValue(columns, row, "Job_Produced"));
            SetModelVar(folder, "Due Date", schedule != null ? FormatDate(schedule.dueDate) : "—");
            SetModelVar(folder, "Job Status", schedule?.jobStatus ?? "—");
            SetModelVar(folder, "Priority", schedule?.priority ?? "—");
        }
        catch (Exception ex)
        {
            Log.Error("JobLogic", $"PopulateModelVariables failed: {ex.Message}");
        }
    }

    private IUANode GetFolder()
    {
        var folder = Project.Current.Get("Model/Plex/Jobs");
        if (folder == null)
            Log.Warning("JobLogic", "Could not find 'Model/Plex/Jobs' folder.");
        return folder;
    }

    private void SetModelVar(IUANode folder, string variableName, string value)
    {
        try
        {
            if (folder == null) return;

            var variable = folder.GetVariable(variableName);
            if (variable == null)
            {
                Log.Warning("JobLogic", $"Model variable '{variableName}' not found in 'Model/Plex/Jobs'.");
                return;
            }
            variable.Value = value ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error("JobLogic", $"SetModelVar({variableName}) failed: {ex.Message}");
        }
    }

    private IUANode ResolveWorkcenterNode()
    {
        var aliasNode = Project.Current.Get("Model/HostSettings/PlexWorkcenter");
        if (aliasNode == null)
        {
            Log.Error("JobLogic", "Could not find 'Model/HostSettings/PlexWorkcenter'.");
            return null;
        }

        var nodeIdVar = aliasNode.GetVariable("PlexWorkcenter");
        if (nodeIdVar == null)
        {
            Log.Error("JobLogic", "Could not find NodeId variable on PlexWorkcenter alias.");
            return null;
        }

        var targetNodeId = (UAManagedCore.NodeId)nodeIdVar.Value;
        if (targetNodeId == null)
        {
            Log.Error("JobLogic", "PlexWorkcenter NodeId value is null.");
            return null;
        }

        var plexWCNode = InformationModel.Get(targetNodeId);
        if (plexWCNode == null)
            Log.Error("JobLogic", $"Could not resolve node from NodeId '{targetNodeId}'.");

        return plexWCNode;
    }

    private int GetWorkcenterKey()
    {
        var node = ResolveWorkcenterNode();
        if (node == null) return -1;

        var keyVar = node.GetVariable("Workcenter_Key");
        if (keyVar == null)
        {
            Log.Error("JobLogic", $"Workcenter_Key not found on '{node.BrowseName}'.");
            return -1;
        }

        if (!int.TryParse(keyVar.Value?.Value?.ToString(), out int key))
        {
            Log.Error("JobLogic", $"Workcenter_Key could not be parsed as integer on '{node.BrowseName}'.");
            return -1;
        }

        return key;
    }

    private string GetWorkcenterLabel()
    {
        var node = ResolveWorkcenterNode();
        return node?.BrowseName ?? "Unknown";
    }

    private (List<string> columns, JArray row)? FetchJobRow(int workcenterKey, string label)
    {
        try
        {
            string jsonBody = JsonConvert.SerializeObject(new
            {
                inputs = new { Workcenter_Key = workcenterKey }
            });

            string response = DatasourcePost($"/api/datasources/{DataSourceId}/execute", jsonBody);
            if (string.IsNullOrEmpty(response))
            {
                Log.Warning("JobLogic", $"{label}: empty datasource response.");
                return null;
            }

            var parsed = JObject.Parse(response);
            var tables = parsed["tables"] as JArray;
            if (tables == null || tables.Count == 0)
            {
                Log.Warning("JobLogic", $"{label}: no tables in datasource response.");
                return null;
            }

            var table = tables[0];
            var columns = table["columns"]?.ToObject<List<string>>();
            var rows = table["rows"] as JArray;

            if (columns == null || rows == null || rows.Count == 0)
            {
                Log.Warning("JobLogic", $"{label}: no rows returned.");
                return null;
            }

            return (columns, rows[0] as JArray);
        }
        catch (Exception ex)
        {
            Log.Error("JobLogic", $"FetchJobRow({label}) failed: {ex.Message}");
            return null;
        }
    }

    private List<SchedulingJobDto> FetchSchedulingJobs()
    {
        try
        {
            var apiKeyVar = LogicObject.GetVariable("apiKey");
            if (apiKeyVar == null)
            {
                Log.Error("JobLogic", "Variable 'apiKey' not found on LogicObject.");
                return new List<SchedulingJobDto>();
            }

            string apiKey = apiKeyVar.Value?.Value?.ToString();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Log.Warning("JobLogic", "apiKey is empty.");
                return new List<SchedulingJobDto>();
            }

            string response = ConnectGet(apiKey, "/scheduling/v1/jobs");
            if (string.IsNullOrEmpty(response))
            {
                Log.Warning("JobLogic", "Empty scheduling response.");
                return new List<SchedulingJobDto>();
            }

            var list = JsonConvert.DeserializeObject<List<SchedulingJobDto>>(response);
            return list ?? new List<SchedulingJobDto>();
        }
        catch (Exception ex)
        {
            Log.Error("JobLogic", $"FetchSchedulingJobs failed: {ex.Message}");
            return new List<SchedulingJobDto>();
        }
    }

    private SchedulingJobDto MatchSchedule((List<string> columns, JArray row)? data, List<SchedulingJobDto> scheduleJobs)
    {
        if (data == null || scheduleJobs == null) return null;

        string jobNo = GetCellValue(data.Value.columns, data.Value.row, "Job_No");
        if (string.IsNullOrWhiteSpace(jobNo) || jobNo == "—") return null;

        return scheduleJobs.FirstOrDefault(s => s.jobNumber == jobNo);
    }

    private string GetCellValue(List<string> columns, JArray row, string colName)
    {
        int idx = columns.IndexOf(colName);
        if (idx < 0 || idx >= row.Count) return "—";
        var val = row[idx];
        if (val == null || val.Type == JTokenType.Null) return "—";
        return val.ToString();
    }

    private string FormatDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "—";
        if (DateTime.TryParse(raw, out var dt))
            return dt.ToString("yyyy-MM-dd");
        return raw;
    }

    private string DatasourcePost(string endpoint, string jsonBody)
    {
        try
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("Authorization", BasicAuthHeader);

                var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
                var response = client.PostAsync(DatasourceURL + endpoint, content).Result;
                string responseBody = response.Content.ReadAsStringAsync().Result;

                if (!response.IsSuccessStatusCode)
                {
                    Log.Error("JobLogic", $"Datasource POST {endpoint} returned {(int)response.StatusCode}: {responseBody}");
                    return null;
                }

                return responseBody;
            }
        }
        catch (Exception ex)
        {
            Log.Error("JobLogic", $"Datasource POST {endpoint} failed: {ex.Message}");
            return null;
        }
    }

    private string ConnectGet(string apiKey, string endpoint)
    {
        try
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("X-Plex-Connect-Api-Key", apiKey);

                var response = client.GetAsync(ConnectURL + endpoint).Result;
                response.EnsureSuccessStatusCode();
                return response.Content.ReadAsStringAsync().Result;
            }
        }
        catch (Exception ex)
        {
            Log.Error("JobLogic", $"Connect GET {endpoint} failed: {ex.Message}");
            return null;
        }
    }

    private class SchedulingJobDto
    {
        public string id { get; set; }
        public string jobNumber { get; set; }
        public string dueDate { get; set; }
        public string jobStatus { get; set; }
        public string priority { get; set; }
        public string jobType { get; set; }
    }
}
