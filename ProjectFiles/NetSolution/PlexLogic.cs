#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.Store;
using FTOptix.NetLogic;
using SysEncoding = System.Text.Encoding;
using FTOptix.NativeUI;
using FTOptix.ODBCStore;
using FTOptix.OPCUAClient;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using FTOptix.OPCUAServer;
#endregion

// =========================================================================
//  PlexLogic - Molding2 / Job 9 station
//
//  This station runs ONE job (job 9, GOLFBALL on Molding2). There is no
//  barcode scanner and no job selection: the workcenter and job are fixed
//  constants. The screen button opens the dialog itself, so this logic never
//  opens or closes dialogs - it only keeps Model/Plex/PlexJob1 populated so
//  whatever the dialog is bound to shows current data.
//
//  What this logic does:
//    1. Keeps PlexJob1 refreshed from the Plex job datasource + Connect API.
//    2. Posts workcenter status (Production / Idle / Off) to the Connect API.
//    3. Posts good parts to Record_Production on every GoodCount increment.
//    4. Posts scrap to Scrap_Add on every ScrapCountProgram increment and on
//       every press of the manual scrap pushbutton.
// =========================================================================

public class PlexLogic : BaseNetLogic
{
    // ---- Plex endpoints / auth ----
    // NOTE: this points at PRODUCTION. Point it at
    // https://kendall-disc.test.on.plex.com while validating, then switch back.
    private const string DatasourceURL = "https://kendall-disc.on.plex.com";
    private const string DataSourceId = "10638";
    private const string BasicAuthHeader = "Basic SXJpc0lBRGlzY3JldGVXc0BwbGV4LmNvbTphMDc5NjkyLWIzMTctNA==";
    private const string ConnectURL = "https://connect.plex.com";

    // =====================================================================
    //  THIS STATION - everything below is fixed to job 9 / Molding2
    // =====================================================================
    private const string JobNo = "9";
    private const int WorkcenterKey = 85954;          // Molding2 (datasource key)
    private const string WorkcenterName = "Molding2";
    private const string PartName = "KENDALL ELECTRIC GOLF BALL";

    // Connect-API workcenter UUID for Molding2 (workcenterCode "Molding2",
    // building IRIS). Status updates are skipped if this is ever blanked out.
    private const string WorkcenterId = "6a8bef3d-1a6b-4b51-bc93-e9e37273df65";

    // ---- Model object the dialog binds to ----
    // Resolved from the 'PlexJob' NodeId variable on this NetLogic; the path
    // below is only a fallback if that pointer is empty.
    private const string PlexJobVariableName = "PlexJob";
    private const string PlexJobFallbackPath = "Model/Plex/PlexJob1";

    // How often the job row / schedule is re-read from Plex. This also
    // re-resolves Job_Key, which changes whenever Plex closes the current job
    // and opens a new one - so don't set it too high or scrap can post against
    // a closed job for up to one interval.
    private const int JobRefreshIntervalMs = 60000;

    // ---- Production recording (driven by the GoodCount PLC tag) ----
    private const string RecordDataSourceId = "20446";
    // PLC name as registered in Plex for THIS workcenter. The workcenter record
    // for Molding2 comes back with plcName:"" - it has no PLC name configured -
    // so "s-2" (which belongs to the Molding1 build) is NOT correct here and is
    // a likely reason production posts were being refused or landing on the
    // wrong workcenter. Sending "" matches what Plex holds; Workcenter_Key is
    // what actually identifies the target.
    //
    // If Record_Production still refuses the transaction, set a PLC Name on the
    // Molding2 workcenter in Plex (Workcenter setup screen) and mirror the exact
    // value here.
    private const string RecordPlcName = "";

    // Writes every datasource request body and response to the FactoryTalk log.
    // Leave on while proving the flow out; turn off once it's trusted.
    private const bool LogPlexPayloads = true;

    // ---- Scrap recording (Scrap_Add) ----
    //   { "inputs": { "Job_Key", "Workcenter_Key", "Part_Key",
    //                 "Part_Operation_Key", "Quantity",
    //                 "Scrap_Reason", "Scrap_Date" } }
    // Returns { "outputs": { "Scrap_Key": <int> } } on success.
    private const string ScrapDataSourceId = "10363";

    // Must match a row in the "Scrap Reason" setup table (part.dbo.scrap_reason).
    // This is the varchar name, NOT the Scrap_Reason_Key.
    private const string ScrapReasonProgram = "Cracks";
    private const string ScrapReasonManual = "Cracks";

    // Quantity added per manual button press.
    private const int ManualScrapQuantity = 1;

    // ScrapCountManual is a momentary pushbutton: the PLC drives it true while
    // held and false on release, so this logic never writes to it. Mechanical
    // contacts can bounce, so a second rising edge inside this window is
    // treated as the same press.
    private const int ManualScrapDebounceMs = 500;

    // ---- Scrap key fallbacks ----
    // Only used when the job datasource doesn't hand the keys back. Job_Key is
    // specific to ONE job instance and goes stale when Plex opens a new job, so
    // treat these as a last resort - the periodic refresh should normally
    // supply them. Leave at 0 to disable the fallback entirely.
    private const int FallbackJobKey = 0;
    private const int FallbackPartKey = 0;
    private const int FallbackPartOperationKey = 0;

    // ---- Controller tag polling ----
    // GoodCount, ScrapCountProgram, ScrapCountManual, Production, Idle and Off
    // are LOCAL variables on this NetLogic that carry a dynamic link to a tag
    // on the EtherNet/IP driver. Handing that local alias to a
    // RemoteVariableSynchronizer does nothing useful - the synchronizer has to
    // be given the actual remote tag. That is almost certainly why nothing has
    // been posting: the cached value never refreshes, so VariableChange never
    // fires and no delta is ever seen.
    //
    // Start() now follows each dynamic link to the real tag and synchronizes
    // and subscribes on THAT.
    private const int TagPollIntervalMs = 500;

    // Belt-and-braces: the counters are also read directly on this interval and
    // pushed through the same delta logic. If the subscription works, these
    // reads see no change and do nothing; if it doesn't, parts still get
    // recorded. A part is never counted twice - both paths compare against the
    // same baseline under the same lock.
    private const int CounterPollIntervalMs = 1000;

    // Optional absolute project paths to the source tags. Leave blank to follow
    // the dynamic link configured on the NetLogic variable (the normal case).
    // Fill one in only if the link can't be resolved automatically, e.g.
    // "CommDrivers/RAEtherNet_IPDriver1/RAEtherNet_IPStation1/Tags/Controller Tags/IMM2/Status/ShotCount"
    private const string GoodCountTagPath = "";
    private const string ScrapCountProgramTagPath = "";
    private const string ScrapCountManualTagPath = "";
    private const string ProductionTagPath = "";
    private const string IdleTagPath = "";
    private const string OffTagPath = "";

    // Refuse to post a counter jump larger than this in a single update. A jump
    // that big means a bad baseline, a counter reset, or a garbage read - not
    // that the machine really made 5000 parts between two polls.
    private const int MaxCounterDelta = 500;

    // ---- Workcenter status IDs (Connect API) ----
    private const string StatusIdle = "5ab64e92-48e6-4ef7-9ea1-59f32c5ecd9e";
    private const string StatusProduction = "41f1c708-f393-4dac-a3f8-fa582d42ab9b";
    private const string StatusOff = "0e5b2fee-aeb9-45c4-9e68-e6633605e939";

    // ---- HTTP ----
    private static readonly HttpClient _httpClient = new HttpClient();

    // Keeps the controller tags refreshed even with nothing bound on screen.
    private RemoteVariableSynchronizer _tagSynchronizer;

    // Vision recording pulse (still wired to Model/IrisLensPub/snapreq).
    private IUAVariable _snapreqVariable;
    private DelayedTask _snapreqResetTask;
    private const int SnapReqPulseMs = 10000;

    // Workcenter status booleans from the PLC.
    private IUAVariable _productionVariable;
    private IUAVariable _idleVariable;
    private IUAVariable _offVariable;

    // ---- Production / scrap counter state ----
    private IUAVariable _goodCountVariable;           // Int32, cumulative PLC shot counter
    private IUAVariable _scrapCountProgramVariable;   // Int32, cumulative PLC reject counter
    private IUAVariable _scrapCountManualVariable;    // Boolean, operator pushbutton
    private readonly object _counterLock = new object();
    // Last value seen on the PLC shot counter. -1 = not yet baselined.
    private int _lastGoodCount = -1;
    // Last value seen on the PLC reject counter. -1 = not yet baselined.
    private int _lastScrapCount = -1;
    // Edge tracking for the momentary scrap button.
    private bool _lastManualScrapState = false;
    private DateTime _lastManualScrapUtc = DateTime.MinValue;
    // Keys used for the next Scrap_Add call; refreshed from Plex.
    private ScrapContext _activeScrapContext = null;

    // ---- Job refresh ----
    private PeriodicTask _jobRefreshTask;
    private PeriodicTask _counterPollTask;
    private LongRunningTask _manualRefreshTask;
    private LongRunningTask _testTask;
    private int _refreshInFlight = 0;   // Interlocked guard against overlap
    private bool _loggedJobColumns = false;
    private bool _loggedScrapContext = false;

    public override void Start()
    {
        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        Log.Info("PlexLogic", "Start() called on '" + LogicObject.BrowseName + "' - fixed to job " +
                              JobNo + " / " + WorkcenterName + " (WC " + WorkcenterKey + ").");

        if (string.IsNullOrEmpty(WorkcenterId))
        {
            Log.Warning("PlexLogic", "WorkcenterId (Connect UUID for " + WorkcenterName +
                                     ") is not set; workcenter status updates will be skipped.");
        }

        Log.Info("PlexLogic", "Config: datasource host " + DatasourceURL +
                              ", job datasource " + DataSourceId +
                              ", Record_Production " + RecordDataSourceId +
                              ", Scrap_Add " + ScrapDataSourceId +
                              ", PLC_Name '" + RecordPlcName + "'" +
                              ", Connect WC " + WorkcenterId + ".");

        _snapreqVariable = LogicObject.GetVariable("snapreq");
        if (_snapreqVariable == null)
            Log.Warning("PlexLogic", "Variable 'snapreq' not found on LogicObject.");

        // ---- Workcenter status booleans ----
        _productionVariable = ResolveSourceVariable("Production", ProductionTagPath);
        if (_productionVariable != null)
            _productionVariable.VariableChange += ProductionVariable_VariableChange;

        _idleVariable = ResolveSourceVariable("Idle", IdleTagPath);
        if (_idleVariable != null)
            _idleVariable.VariableChange += IdleVariable_VariableChange;

        _offVariable = ResolveSourceVariable("Off", OffTagPath);
        if (_offVariable != null)
            _offVariable.VariableChange += OffVariable_VariableChange;

        // ---- Production source ----
        _goodCountVariable = ResolveSourceVariable("GoodCount", GoodCountTagPath);
        if (_goodCountVariable == null)
        {
            Log.Error("PlexLogic", "GoodCount could not be resolved; production will NOT be recorded.");
        }
        else
        {
            _goodCountVariable.VariableChange += GoodCountVariable_VariableChange;
            Log.Info("PlexLogic", "Subscribed to GoodCount for production recording.");
        }

        // ---- Scrap sources ----
        _scrapCountProgramVariable = ResolveSourceVariable("ScrapCountProgram", ScrapCountProgramTagPath);
        if (_scrapCountProgramVariable != null)
            _scrapCountProgramVariable.VariableChange += ScrapCountProgramVariable_VariableChange;

        _scrapCountManualVariable = ResolveSourceVariable("ScrapCountManual", ScrapCountManualTagPath);
        if (_scrapCountManualVariable != null)
        {
            // Seed the edge tracker so a button already held at startup doesn't
            // immediately post a scrap record.
            _lastManualScrapState = ReadBool(_scrapCountManualVariable);
            _scrapCountManualVariable.VariableChange += ScrapCountManualVariable_VariableChange;
        }

        // Both counters baseline on their FIRST synchronized read rather than
        // here. At this point the tags haven't been polled yet, so the cached
        // value is usually 0 - baselining off that would make the first real
        // read look like a delta of the entire shift total.
        lock (_counterLock)
        {
            _lastGoodCount = -1;
            _lastScrapCount = -1;
        }

        // Nothing on screen is bound to the controller tags, so start polling.
        SetupTagSynchronizer();

        // Direct-read safety net in case the subscription still doesn't fire.
        _counterPollTask = new PeriodicTask(CounterPoll, CounterPollIntervalMs, LogicObject);
        _counterPollTask.Start();

        // Pull the job data once now (off the startup thread - the datasource
        // call is synchronous and would otherwise stall project start), then
        // keep it refreshed.
        RefreshJob();

        _jobRefreshTask = new PeriodicTask(JobRefreshPeriodic, JobRefreshIntervalMs, LogicObject);
        _jobRefreshTask.Start();
    }

    public override void Stop()
    {
        // Stop polling first so no change events arrive mid-teardown.
        if (_tagSynchronizer != null)
        {
            _tagSynchronizer.Dispose();
            _tagSynchronizer = null;
        }

        if (_jobRefreshTask != null)
        {
            _jobRefreshTask.Dispose();
            _jobRefreshTask = null;
        }

        if (_counterPollTask != null)
        {
            _counterPollTask.Dispose();
            _counterPollTask = null;
        }

        if (_manualRefreshTask != null)
        {
            _manualRefreshTask.Dispose();
            _manualRefreshTask = null;
        }

        if (_testTask != null)
        {
            _testTask.Dispose();
            _testTask = null;
        }

        if (_productionVariable != null)
            _productionVariable.VariableChange -= ProductionVariable_VariableChange;

        if (_idleVariable != null)
            _idleVariable.VariableChange -= IdleVariable_VariableChange;

        if (_offVariable != null)
            _offVariable.VariableChange -= OffVariable_VariableChange;

        if (_goodCountVariable != null)
            _goodCountVariable.VariableChange -= GoodCountVariable_VariableChange;

        if (_scrapCountProgramVariable != null)
            _scrapCountProgramVariable.VariableChange -= ScrapCountProgramVariable_VariableChange;

        if (_scrapCountManualVariable != null)
            _scrapCountManualVariable.VariableChange -= ScrapCountManualVariable_VariableChange;

        if (_snapreqResetTask != null)
        {
            _snapreqResetTask.Dispose();
            _snapreqResetTask = null;
        }
    }

    // =====================================================================
    //  Tag polling
    // =====================================================================

    // Creates the one synchronizer that keeps the controller tags refreshed.
    // Without this, VariableChange never fires for anything on the EtherNet/IP
    // driver unless a page happens to have a widget bound to that tag.
    private void SetupTagSynchronizer()
    {
        try
        {
            _tagSynchronizer = new RemoteVariableSynchronizer(TimeSpan.FromMilliseconds(TagPollIntervalMs));

            int added = 0;
            added += AddToSynchronizer(_productionVariable, "Production");
            added += AddToSynchronizer(_idleVariable, "Idle");
            added += AddToSynchronizer(_offVariable, "Off");
            added += AddToSynchronizer(_goodCountVariable, "GoodCount");
            added += AddToSynchronizer(_scrapCountProgramVariable, "ScrapCountProgram");
            added += AddToSynchronizer(_scrapCountManualVariable, "ScrapCountManual");

            Log.Info("PlexLogic", "Tag synchronizer started: " + added + " tag(s) polled every " +
                                  TagPollIntervalMs + "ms.");
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "SetupTagSynchronizer failed: " + ex.Message +
                                   " - status and counter tags will NOT update.");
        }
    }

    // Adds one variable to the synchronizer, returning 1 if it went in.
    private int AddToSynchronizer(IUAVariable variable, string name)
    {
        if (variable == null)
        {
            Log.Warning("PlexLogic", "Not synchronizing '" + name + "': variable missing.");
            return 0;
        }

        try
        {
            _tagSynchronizer.Add(variable);
            return 1;
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "Could not synchronize '" + name + "': " + ex.Message);
            return 0;
        }
    }

    // =====================================================================
    //  Job refresh - keeps PlexJob1 and the scrap keys current
    // =====================================================================

    // Callable from the screen (e.g. from the same button that opens the
    // dialog) to force an immediate re-read before the dialog is shown.
    [ExportMethod]
    public void RefreshJob()
    {
        try
        {
            if (_manualRefreshTask != null)
                _manualRefreshTask.Dispose();

            _manualRefreshTask = new LongRunningTask(RefreshJobLongRunning, LogicObject);
            _manualRefreshTask.Start();
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "RefreshJob failed to start: " + ex.Message);
        }
    }

    private void RefreshJobLongRunning(LongRunningTask task)
    {
        RefreshJobData();
    }

    private void JobRefreshPeriodic(PeriodicTask task)
    {
        RefreshJobData();
    }

    // Re-reads the job row and the schedule entry, updates the scrap keys and
    // repopulates PlexJob1. Never touches the counter baselines - doing that
    // here would silently drop parts made between refreshes.
    private void RefreshJobData()
    {
        // A slow datasource call must not stack up behind the periodic task.
        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
            return;

        try
        {
            JobRow row = FetchJobRowByWorkcenterKey(WorkcenterKey);
            SchedulingJobDto sched = FetchSchedulingJob(JobNo);

            UpdateScrapContext(row);
            PopulatePlexJob(row, sched);
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "RefreshJobData failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    // Works out which keys scrap postings should use. Prefers the live
    // datasource row; falls back to the constants above only if they're set.
    private void UpdateScrapContext(JobRow row)
    {
        ScrapContext ctx = TryBuildScrapContextFromRow(row);
        string source = "job datasource";

        if (ctx == null && FallbackJobKey > 0 && FallbackPartKey > 0 && FallbackPartOperationKey > 0)
        {
            ctx = new ScrapContext(FallbackJobKey, FallbackPartKey, FallbackPartOperationKey, WorkcenterKey);
            source = "local fallback constants";
        }

        ScrapContext previous;
        lock (_counterLock)
        {
            previous = _activeScrapContext;
            _activeScrapContext = ctx;
        }

        if (ctx == null)
        {
            // Log the first time and on any transition back to "no keys", but
            // not on every 60s refresh.
            if (previous != null || !_loggedScrapContext)
            {
                _loggedScrapContext = true;
                Log.Warning("PlexLogic", "No scrap keys available for job " + JobNo + " - SCRAP WILL NOT POST. " +
                                         "The job datasource did not return usable Job_Key / Part_Key / " +
                                         "Part_Operation_Key. Check the 'Job datasource columns' log line, " +
                                         "then either add those columns to datasource " + DataSourceId +
                                         " or fill in FallbackJobKey / FallbackPartKey / FallbackPartOperationKey.");
            }
            return;
        }

        // Only log when something actually changed, so the periodic refresh
        // doesn't fill the log with identical lines.
        if (previous == null || !previous.SameAs(ctx))
        {
            _loggedScrapContext = true;
            Log.Info("PlexLogic", "Scrap keys for job " + JobNo + " from " + source +
                                  ": Job_Key " + ctx.JobKey +
                                  ", Part_Key " + ctx.PartKey +
                                  ", Part_Operation_Key " + ctx.PartOperationKey +
                                  ", Workcenter_Key " + ctx.WorkcenterKey + ".");
        }
    }

    // Pulls Job_Key / Part_Key / Part_Operation_Key from the job datasource row
    // if that datasource returns them. Returns null when any are missing.
    private static ScrapContext TryBuildScrapContextFromRow(JobRow row)
    {
        if (row == null)
            return null;

        long jobKey, partKey, partOpKey;
        if (!TryParsePlexInt(row.GetValue("Job_Key"), out jobKey)) return null;
        if (!TryParsePlexInt(row.GetValue("Part_Key"), out partKey)) return null;
        if (!TryParsePlexInt(row.GetValue("Part_Operation_Key"), out partOpKey)) return null;

        if (jobKey <= 0 || partKey <= 0 || partOpKey <= 0)
            return null;

        return new ScrapContext((int)jobKey, (int)partKey, (int)partOpKey, WorkcenterKey);
    }

    // =====================================================================
    //  PlexJob object population (the dialog binds to this object)
    // =====================================================================

    private void PopulatePlexJob(JobRow row, SchedulingJobDto schedule)
    {
        try
        {
            IUANode plexJob = GetPlexJob();
            if (plexJob == null) return;

            SetObjectVar(plexJob, "PartName", PartName);
            SetObjectVar(plexJob, "Job", JobNo);
            SetObjectVar(plexJob, "Workcenter", WorkcenterName);

            // Job Status from the datasource row; Priority + DueDate from ConnectAPI.
            string status = row != null ? row.GetValue("Status") : "-";
            SetObjectVar(plexJob, "JobStatus", !string.IsNullOrEmpty(status) ? status : "-");
            SetObjectVar(plexJob, "Priority", schedule != null ? schedule.priority : "-");
            SetObjectVar(plexJob, "DueDate", (schedule != null && !string.IsNullOrEmpty(schedule.dueDate)) ? schedule.dueDate : "-");

            // Part number and quantities come from the datasource row.
            string partNumber = row != null ? row.GetValue("Simple_Part_No") : "-";
            string target = row != null ? row.GetValue("Job_Quantity") : null;
            string completed = row != null ? row.GetValue("Job_Produced") : null;

            SetObjectVar(plexJob, "PartNumber", !string.IsNullOrEmpty(partNumber) ? partNumber : "-");
            SetObjectVar(plexJob, "TargetQuantity", FormatQuantity(target));
            SetObjectVar(plexJob, "QuantityCompleted", FormatQuantity(completed));
            SetObjectVar(plexJob, "Target", FormatQuantity(target));

            string operation = row != null ? row.GetValue("Operation_No") : "-";
            SetObjectVar(plexJob, "Operation", operation);
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "PopulatePlexJob failed: " + ex.Message);
        }
    }

    // Resolves the PlexJob object from the NodeId variable on this NetLogic,
    // falling back to the fixed project path.
    private IUANode GetPlexJob()
    {
        try
        {
            IUAVariable pointer = LogicObject.GetVariable(PlexJobVariableName);
            if (pointer != null && pointer.Value != null)
            {
                NodeId id = pointer.Value.Value as NodeId;
                if (id != null)
                {
                    IUANode node = InformationModel.Get(id);
                    if (node != null)
                        return node;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("PlexLogic", "Could not resolve the '" + PlexJobVariableName +
                                     "' pointer: " + ex.Message);
        }

        IUANode fallback = Project.Current.Get(PlexJobFallbackPath);
        if (fallback == null)
        {
            Log.Warning("PlexLogic", "Could not find the PlexJob object via the '" + PlexJobVariableName +
                                     "' pointer or at '" + PlexJobFallbackPath + "'.");
        }
        return fallback;
    }

    private void SetObjectVar(IUANode plexJob, string variableName, string value)
    {
        try
        {
            if (plexJob == null) return;

            IUAVariable variable = plexJob.GetVariable(variableName);
            if (variable == null)
            {
                Log.Warning("PlexLogic", "Property '" + variableName + "' not found on the PlexJob object.");
                return;
            }
            variable.Value = value != null ? value : string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "SetObjectVar(" + variableName + ") failed: " + ex.Message);
        }
    }

    // =====================================================================
    //  Tag resolution and polling
    // =====================================================================

    // Returns the variable that should actually be synchronized and subscribed
    // to for a given NetLogic variable name.
    //
    // Order of preference:
    //   1. The absolute path override, if one is configured.
    //   2. The target of the dynamic link on the NetLogic variable - this is
    //      the real driver tag, and it is what the synchronizer needs.
    //   3. The local variable itself, as a last resort.
    private IUAVariable ResolveSourceVariable(string localName, string overridePath)
    {
        IUAVariable local = LogicObject.GetVariable(localName);
        if (local == null)
        {
            Log.Error("PlexLogic", "Variable '" + localName + "' not found on LogicObject.");
            return null;
        }

        // 1. Explicit path override.
        if (!string.IsNullOrEmpty(overridePath))
        {
            try
            {
                IUAVariable target = Project.Current.Get(overridePath) as IUAVariable;
                if (target != null)
                {
                    Log.Info("PlexLogic", localName + " -> tag at configured path '" + overridePath + "'.");
                    return target;
                }
                Log.Warning("PlexLogic", localName + ": configured path '" + overridePath +
                                         "' did not resolve to a variable.");
            }
            catch (Exception ex)
            {
                Log.Warning("PlexLogic", localName + ": configured path lookup failed: " + ex.Message);
            }
        }

        // 2. Follow the dynamic link.
        try
        {
            IUAVariable link = local.GetVariable("DynamicLink");
            if (link != null && link.Value != null && link.Value.Value != null)
            {
                string path = link.Value.Value.ToString();
                if (!string.IsNullOrEmpty(path))
                {
                    var resolved = LogicObject.Context.ResolvePath(local, path);
                    IUAVariable target = (resolved != null) ? resolved.ResolvedNode as IUAVariable : null;
                    if (target != null)
                    {
                        Log.Info("PlexLogic", localName + " -> resolved through its dynamic link to '" +
                                              target.BrowseName + "'.");
                        return target;
                    }

                    Log.Warning("PlexLogic", localName + ": dynamic link '" + path +
                                             "' did not resolve to a variable.");
                }
            }
            else
            {
                Log.Warning("PlexLogic", localName + " has no dynamic link; using the local variable.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning("PlexLogic", localName + ": could not follow the dynamic link (" + ex.Message +
                                     "). Falling back to the local variable - if this tag never updates, " +
                                     "set its absolute path in the *TagPath constants.");
        }

        // 3. Local variable.
        return local;
    }

    // Reads the counters directly and pushes them through the same delta logic
    // the change events use. Idempotent: an unchanged value does nothing.
    private void CounterPoll(PeriodicTask task)
    {
        try
        {
            int value;

            if (TryReadInt(_goodCountVariable, out value))
                ProcessGoodCount(value);

            if (TryReadInt(_scrapCountProgramVariable, out value))
                ProcessScrapCount(value);

            if (_scrapCountManualVariable != null)
                ProcessManualScrap(ReadBool(_scrapCountManualVariable));
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "CounterPoll failed: " + ex.Message);
        }
    }

    // =====================================================================
    //  Commissioning helpers - wire these to temporary screen buttons
    // =====================================================================

    // Posts a single good part to Plex right now, bypassing the PLC counter.
    // Proves the Record_Production leg on its own.
    [ExportMethod]
    public void TestRecordProduction()
    {
        RunInBackground(delegate
        {
            Log.Info("PlexLogic", "TEST: posting 1 good part to " + WorkcenterName + ".");
            RecordProduction(1);
        });
    }

    // Posts a single scrap part right now, bypassing the PLC counter/button.
    [ExportMethod]
    public void TestRecordScrap()
    {
        RunInBackground(delegate
        {
            Log.Info("PlexLogic", "TEST: posting 1 scrap part to " + WorkcenterName + ".");
            RecordScrap(1, ScrapReasonManual, "test button");
        });
    }

    // Forces an Idle status post, proving the Connect API leg on its own.
    [ExportMethod]
    public void TestSetStatusIdle()
    {
        RunInBackground(delegate
        {
            Log.Info("PlexLogic", "TEST: setting " + WorkcenterName + " status to Idle.");
            SetWorkcenterStatus(StatusIdle, "Idle");
        });
    }

    // Dumps everything needed to work out why nothing is posting: whether the
    // tags are actually reading, where the baselines sit, and whether the scrap
    // keys resolved.
    [ExportMethod]
    public void LogDiagnostics()
    {
        try
        {
            int good, scrapCount;
            bool haveGood = TryReadInt(_goodCountVariable, out good);
            bool haveScrap = TryReadInt(_scrapCountProgramVariable, out scrapCount);

            int lastGood, lastScrap;
            ScrapContext ctx;
            lock (_counterLock)
            {
                lastGood = _lastGoodCount;
                lastScrap = _lastScrapCount;
                ctx = _activeScrapContext;
            }

            Log.Info("PlexLogic", "DIAG tags: GoodCount=" + (haveGood ? good.ToString() : "(no read)") +
                                  " baseline=" + lastGood +
                                  " | ScrapCountProgram=" + (haveScrap ? scrapCount.ToString() : "(no read)") +
                                  " baseline=" + lastScrap +
                                  " | ScrapCountManual=" + ReadBool(_scrapCountManualVariable) +
                                  " | Production=" + ReadBool(_productionVariable) +
                                  " Idle=" + ReadBool(_idleVariable) +
                                  " Off=" + ReadBool(_offVariable));

            Log.Info("PlexLogic", "DIAG synchronizer: " + (_tagSynchronizer != null ? "running" : "NOT RUNNING") +
                                  " | apiKey " + (string.IsNullOrWhiteSpace(GetConnectApiKey()) ? "EMPTY" : "present") +
                                  " | PlexJob object " + (GetPlexJob() != null ? "resolved" : "NOT FOUND"));

            if (ctx == null)
                Log.Warning("PlexLogic", "DIAG scrap keys: none resolved - scrap cannot post.");
            else
                Log.Info("PlexLogic", "DIAG scrap keys: Job_Key " + ctx.JobKey +
                                      ", Part_Key " + ctx.PartKey +
                                      ", Part_Operation_Key " + ctx.PartOperationKey +
                                      ", Workcenter_Key " + ctx.WorkcenterKey + ".");
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "LogDiagnostics failed: " + ex.Message);
        }
    }

    // Runs a blocking Plex call off the UI thread.
    private void RunInBackground(Action work)
    {
        try
        {
            if (_testTask != null)
                _testTask.Dispose();

            _testTask = new LongRunningTask(delegate (LongRunningTask task)
            {
                try { work(); }
                catch (Exception inner) { Log.Error("PlexLogic", "Background task failed: " + inner.Message); }
            }, LogicObject);
            _testTask.Start();
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "RunInBackground failed: " + ex.Message);
        }
    }

    // Safe int read from an IUAVariable.
    private static bool TryReadInt(IUAVariable variable, out int value)
    {
        value = 0;
        try
        {
            if (variable == null || variable.Value == null || variable.Value.Value == null)
                return false;
            value = Convert.ToInt32(variable.Value.Value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // =====================================================================
    //  Vision snapshot request (optional; call from a screen button)
    // =====================================================================

    [ExportMethod]
    public void TriggerSnapReq()
    {
        try
        {
            if (_snapreqVariable == null)
                return;

            _snapreqVariable.Value = true;
            Log.Info("PlexLogic", "snapreq set true; will reset in " + (SnapReqPulseMs / 1000) + "s.");

            if (_snapreqResetTask != null)
                _snapreqResetTask.Dispose();

            _snapreqResetTask = new DelayedTask(ResetSnapReq, SnapReqPulseMs, LogicObject);
            _snapreqResetTask.Start();
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "TriggerSnapReq failed: " + ex.Message);
        }
    }

    private void ResetSnapReq(DelayedTask task)
    {
        try
        {
            if (_snapreqVariable != null)
                _snapreqVariable.Value = false;
            Log.Info("PlexLogic", "snapreq reset to false.");
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "ResetSnapReq failed: " + ex.Message);
        }
    }

    // =====================================================================
    //  Workcenter status monitoring (Production / Idle / Off booleans)
    // =====================================================================

    private void ProductionVariable_VariableChange(object sender, VariableChangeEventArgs e)
    {
        if (IsTrue(e))
            SetWorkcenterStatus(StatusProduction, "Production");
    }

    private void IdleVariable_VariableChange(object sender, VariableChangeEventArgs e)
    {
        if (IsTrue(e))
            SetWorkcenterStatus(StatusIdle, "Idle");
    }

    private void OffVariable_VariableChange(object sender, VariableChangeEventArgs e)
    {
        if (IsTrue(e))
            SetWorkcenterStatus(StatusOff, "Off");
    }

    // True only when the new value is true.
    private static bool IsTrue(VariableChangeEventArgs e)
    {
        try
        {
            if (e.NewValue == null || e.NewValue.Value == null)
                return false;
            return Convert.ToBoolean(e.NewValue.Value);
        }
        catch
        {
            return false;
        }
    }

    // Posts a status update for this workcenter.
    private void SetWorkcenterStatus(string statusId, string statusName)
    {
        try
        {
            if (string.IsNullOrEmpty(WorkcenterId))
            {
                Log.Warning("PlexLogic", "Status '" + statusName + "' not sent: the Connect UUID for " +
                                         WorkcenterName + " is not configured.");
                return;
            }

            string apiKey = GetConnectApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Log.Warning("PlexLogic", "apiKey is empty; cannot set workcenter status.");
                return;
            }

            string body = "{\"workcenterStatusId\":" + JsonString(statusId) + "}";
            string endpoint = "/production/v1/control/workcenters/" + WorkcenterId + "/status";

            if (LogPlexPayloads)
                Log.Info("PlexLogic", "Status REQUEST -> POST " + ConnectURL + endpoint + " " + body);

            string response = ConnectPost(apiKey, endpoint, body);
            if (response == null)
            {
                Log.Warning("PlexLogic", "Status '" + statusName + "' update failed for " + WorkcenterName + ".");
                return;
            }

            Log.Info("PlexLogic", WorkcenterName + " status set to '" + statusName + "'." +
                                  (LogPlexPayloads ? " Response: " + Truncate(response, 400) : ""));
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "SetWorkcenterStatus(" + statusName + ") failed: " + ex.Message);
        }
    }

    // =====================================================================
    //  Production recording  (Record_Production / datasource 20446)
    // =====================================================================

    // PLC shot counter. The tag is cumulative, so post the delta only.
    private void GoodCountVariable_VariableChange(object sender, VariableChangeEventArgs e)
    {
        try
        {
            if (e.NewValue == null || e.NewValue.Value == null)
                return;

            int newCount;
            try { newCount = Convert.ToInt32(e.NewValue.Value); }
            catch { return; }

            ProcessGoodCount(newCount);
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "GoodCountVariable_VariableChange failed: " + ex.Message);
        }
    }

    // Turns a new cumulative shot count into a production post. Called from
    // both the change event and the poll; safe to call with the same value
    // repeatedly.
    private void ProcessGoodCount(int newCount)
    {
        try
        {
            int delta;
            lock (_counterLock)
            {
                if (_lastGoodCount < 0)
                {
                    _lastGoodCount = newCount;
                    Log.Info("PlexLogic", "Good counter baselined at " + newCount + " (first read).");
                    return;
                }

                if (newCount == _lastGoodCount)
                    return;

                if (newCount < _lastGoodCount)
                {
                    // Counter reset (shift/job change) or rollover. Rebase and
                    // post nothing rather than inventing production.
                    Log.Info("PlexLogic", "Good counter went backwards (" + _lastGoodCount +
                                          " -> " + newCount + "); rebaselined, no production posted.");
                    _lastGoodCount = newCount;
                    return;
                }

                delta = newCount - _lastGoodCount;
                _lastGoodCount = newCount;
            }

            if (delta > MaxCounterDelta)
            {
                Log.Error("PlexLogic", "GoodCount jumped +" + delta + " in one update, over the " +
                                       MaxCounterDelta + " sanity limit. Nothing posted; baseline moved to " +
                                       newCount + ". Check the tag if the machine really ran that much.");
                return;
            }

            RecordProduction(delta);
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "ProcessGoodCount failed: " + ex.Message);
        }
    }

    // Posts one production record for this workcenter.
    // Scrap_Quantity stays 0 here on purpose: scrap goes through Scrap_Add
    // (10363) instead, so sending it on both paths would double-count.
    private void RecordProduction(int quantity)
    {
        if (quantity <= 0)
            return;

        StringBuilder sb = new StringBuilder();
        sb.Append("{\"inputs\":{");
        sb.Append("\"Workcenter_Key\":").Append(WorkcenterKey).Append(",");
        sb.Append("\"PLC_Name\":").Append(JsonString(RecordPlcName)).Append(",");
        sb.Append("\"Quantity\":").Append(quantity).Append(",");
        sb.Append("\"Container_Full\":false,");
        sb.Append("\"Container_Status\":\"OK\",");
        sb.Append("\"Container_Note\":\"\",");
        sb.Append("\"Scrap_Quantity\":0,");
        sb.Append("\"Scrap_Reason\":\"\",");
        sb.Append("\"Add_To_Master\":0,");
        sb.Append("\"Master_Unit_No\":\"NEW\",");
        sb.Append("\"Validate_Only\":false");
        sb.Append("}}");

        string response = DatasourcePost("/api/datasources/" + RecordDataSourceId + "/execute",
                                         sb.ToString(), "Record_Production");
        if (response == null)
        {
            Log.Warning("PlexLogic", "Production post FAILED: qty " + quantity +
                                     ", job " + JobNo + ", " + WorkcenterName + ".");
            return;
        }

        // Record_Production answers 200 even when it refuses the transaction,
        // so the real outcome is in outputs.Result_Error / Result_Message.
        string resultMessage;
        if (IsProductionResultError(response, out resultMessage))
        {
            Log.Warning("PlexLogic", "Production post REJECTED by Plex: qty " + quantity +
                                     ", job " + JobNo + ", " + WorkcenterName +
                                     " - " + (resultMessage ?? "(no message)"));
            return;
        }

        Log.Info("PlexLogic", "Production recorded: qty " + quantity +
                              ", job " + JobNo + ", " + WorkcenterName + ".");
    }

    // True when Record_Production came back with Result_Error set.
    private static bool IsProductionResultError(string json, out string message)
    {
        message = null;
        try
        {
            JsonValue root = JsonValue.Parse(json);
            if (root == null || !root.IsObject) return false;

            JsonValue outputs = root.GetProperty("outputs");
            if (outputs == null || !outputs.IsObject) return false;

            JsonValue msg = outputs.GetProperty("Result_Message");
            if (msg != null) message = msg.AsString();

            JsonValue err = outputs.GetProperty("Result_Error");
            if (err == null) return false;

            return string.Equals(err.AsString(), "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // =====================================================================
    //  Scrap recording  (Scrap_Add / datasource 10363)
    // =====================================================================

    // PLC reject counter. The tag is cumulative, so post the delta only.
    private void ScrapCountProgramVariable_VariableChange(object sender, VariableChangeEventArgs e)
    {
        try
        {
            if (e.NewValue == null || e.NewValue.Value == null)
                return;

            int newCount;
            try { newCount = Convert.ToInt32(e.NewValue.Value); }
            catch { return; }

            ProcessScrapCount(newCount);
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "ScrapCountProgramVariable_VariableChange failed: " + ex.Message);
        }
    }

    // Turns a new cumulative reject count into a scrap post.
    private void ProcessScrapCount(int newCount)
    {
        try
        {
            int delta;
            lock (_counterLock)
            {
                if (_lastScrapCount < 0)
                {
                    _lastScrapCount = newCount;
                    Log.Info("PlexLogic", "Scrap counter baselined at " + newCount + " (first read).");
                    return;
                }

                if (newCount == _lastScrapCount)
                    return;

                if (newCount < _lastScrapCount)
                {
                    // Counter reset (shift/job change) or rollover. Rebase and
                    // post nothing - guessing at the missed count would invent
                    // scrap records.
                    Log.Info("PlexLogic", "Scrap counter went backwards (" + _lastScrapCount +
                                          " -> " + newCount + "); rebaselined, no scrap posted.");
                    _lastScrapCount = newCount;
                    return;
                }

                delta = newCount - _lastScrapCount;
                _lastScrapCount = newCount;
            }

            if (delta > MaxCounterDelta)
            {
                Log.Error("PlexLogic", "ScrapCountProgram jumped +" + delta + " in one update, over the " +
                                       MaxCounterDelta + " sanity limit. Nothing posted; baseline moved to " +
                                       newCount + ".");
                return;
            }

            RecordScrap(delta, ScrapReasonProgram, "PLC reject counter");
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "ProcessScrapCount failed: " + ex.Message);
        }
    }

    // Operator scrap pushbutton (momentary). The PLC drives the tag true while
    // the button is held and false when released, so this only watches for the
    // false->true edge and never writes to the tag. One press = one part,
    // however long it's held down.
    private void ScrapCountManualVariable_VariableChange(object sender, VariableChangeEventArgs e)
    {
        ProcessManualScrap(IsTrue(e));
    }

    private void ProcessManualScrap(bool now)
    {
        try
        {
            bool pressed;
            bool bounced = false;

            lock (_counterLock)
            {
                if (now == _lastManualScrapState)
                    return;                      // no edge; nothing to do

                pressed = now;                   // true here means false -> true
                _lastManualScrapState = now;

                if (pressed)
                {
                    DateTime utcNow = DateTime.UtcNow;
                    if ((utcNow - _lastManualScrapUtc).TotalMilliseconds < ManualScrapDebounceMs)
                        bounced = true;
                    else
                        _lastManualScrapUtc = utcNow;
                }
            }

            if (!pressed)
            {
                Log.Info("PlexLogic", "Manual scrap button released.");
                return;
            }

            if (bounced)
            {
                Log.Info("PlexLogic", "Manual scrap press ignored: within the " +
                                      ManualScrapDebounceMs + "ms bounce window.");
                return;
            }

            Log.Info("PlexLogic", "Manual scrap button pressed; recording " + ManualScrapQuantity + ".");
            RecordScrap(ManualScrapQuantity, ScrapReasonManual, "manual button");
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "ProcessManualScrap failed: " + ex.Message);
        }
    }

    // Posts one Scrap_Add transaction against the active job.
    private void RecordScrap(int quantity, string scrapReason, string source)
    {
        if (quantity <= 0)
            return;

        ScrapContext ctx;
        lock (_counterLock) { ctx = _activeScrapContext; }

        if (ctx == null)
        {
            Log.Warning("PlexLogic", "Scrap from " + source + " (qty " + quantity +
                                     ") ignored: no Job_Key / Part_Key / Part_Operation_Key available " +
                                     "for job " + JobNo + ".");
            return;
        }

        StringBuilder sb = new StringBuilder();
        sb.Append("{\"inputs\":{");
        sb.Append("\"Job_Key\":").Append(ctx.JobKey).Append(",");
        sb.Append("\"Workcenter_Key\":").Append(ctx.WorkcenterKey).Append(",");
        sb.Append("\"Part_Key\":").Append(ctx.PartKey).Append(",");
        sb.Append("\"Part_Operation_Key\":").Append(ctx.PartOperationKey).Append(",");
        sb.Append("\"Quantity\":").Append(quantity).Append(",");
        sb.Append("\"Scrap_Reason\":").Append(JsonString(scrapReason)).Append(",");
        sb.Append("\"Scrap_Date\":").Append(JsonString(PlexUtcNow()));
        sb.Append("}}");

        string response = DatasourcePost("/api/datasources/" + ScrapDataSourceId + "/execute",
                                         sb.ToString(), "Scrap_Add");
        if (response == null)
        {
            Log.Warning("PlexLogic", "Scrap post FAILED (" + source + ", qty " + quantity +
                                     ", reason '" + scrapReason + "', " + WorkcenterName + ").");
            return;
        }

        string scrapKey = ParseScrapKey(response);
        if (!string.IsNullOrEmpty(scrapKey) && scrapKey != "-")
        {
            Log.Info("PlexLogic", "Scrap recorded (" + source + "): qty " + quantity +
                                  ", reason '" + scrapReason + "', Scrap_Key " + scrapKey + ".");
        }
        else
        {
            // 2xx with no Scrap_Key usually means the stored procedure rejected
            // it; the payload carries the reason.
            Log.Warning("PlexLogic", "Scrap post returned no Scrap_Key (" + source +
                                     "). Response: " + response);
        }
    }

    // Scrap_Date format Plex accepts: 2026-07-27T13:39:21.038Z
    private static string PlexUtcNow()
    {
        return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
                                        System.Globalization.CultureInfo.InvariantCulture);
    }

    // Reads outputs.Scrap_Key out of a Scrap_Add response.
    private static string ParseScrapKey(string json)
    {
        try
        {
            JsonValue root = JsonValue.Parse(json);
            if (root == null || !root.IsObject) return null;

            JsonValue outputs = root.GetProperty("outputs");
            if (outputs == null || !outputs.IsObject) return null;

            JsonValue key = outputs.GetProperty("Scrap_Key");
            return key != null ? key.AsString() : null;
        }
        catch
        {
            return null;
        }
    }

    // =====================================================================
    //  Value helpers
    // =====================================================================

    // Safe bool read from an IUAVariable.
    private static bool ReadBool(IUAVariable variable)
    {
        try
        {
            if (variable == null || variable.Value == null || variable.Value.Value == null)
                return false;
            return Convert.ToBoolean(variable.Value.Value);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParsePlexInt(string raw, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(raw)) return false;

        string s = raw.Trim();
        if (s == "-") return false;

        // Drop a decimal portion if present.
        int dot = s.IndexOf('.');
        if (dot >= 0)
            s = s.Substring(0, dot);

        long parsed;
        if (long.TryParse(s, System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.InvariantCulture, out parsed))
        {
            value = parsed;
            return true;
        }

        // Fallback: parse as double (handles unexpected formats), then truncate.
        double d;
        if (double.TryParse(raw, System.Globalization.NumberStyles.Any,
                           System.Globalization.CultureInfo.InvariantCulture, out d))
        {
            value = (long)Math.Floor(d);
            return true;
        }

        return false;
    }

    // Formats a Plex numeric string (e.g. "10000.000000") as a clean integer
    // for display. Returns "-" for empty/non-numeric input.
    private static string FormatQuantity(string raw)
    {
        long v;
        if (!TryParsePlexInt(raw, out v)) return "-";
        return v.ToString();
    }

    // =====================================================================
    //  Plex datasource / connect fetch  (manual JSON parsing, no external deps)
    // =====================================================================

    private JobRow FetchJobRowByWorkcenterKey(int workcenterKey)
    {
        try
        {
            string jsonBody = "{\"inputs\":{\"Workcenter_Key\":" + workcenterKey + "}}";

            string response = DatasourcePost("/api/datasources/" + DataSourceId + "/execute", jsonBody);
            if (string.IsNullOrEmpty(response))
            {
                Log.Warning("PlexLogic", "WC " + workcenterKey + ": empty datasource response.");
                return null;
            }

            // Expected shape: { "tables":[ { "columns":[...], "rows":[[...],...] } ] }
            List<string> columns;
            List<string> firstRow;
            if (!TryParseFirstTableRow(response, out columns, out firstRow))
            {
                Log.Warning("PlexLogic", "WC " + workcenterKey + ": no rows returned. " +
                                         "Nothing will populate PlexJob1 and scrap has no keys to post against.");
                return null;
            }

            // One-time visibility into what this datasource actually returns.
            // If Job_Key / Part_Key / Part_Operation_Key are NOT in this list,
            // scrap can never post without the fallback constants being filled.
            if (!_loggedJobColumns)
            {
                _loggedJobColumns = true;
                Log.Info("PlexLogic", "Job datasource columns: " + string.Join(", ", columns));
            }

            return new JobRow(columns, firstRow);
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "FetchJobRowByWorkcenterKey(" + workcenterKey + ") failed: " + ex.Message);
            return null;
        }
    }

    private SchedulingJobDto FetchSchedulingJob(string jobNo)
    {
        try
        {
            string apiKey = GetConnectApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Log.Warning("PlexLogic", "apiKey is empty; skipping scheduling fetch.");
                return null;
            }

            string response = ConnectGet(apiKey, "/scheduling/v1/jobs");
            if (string.IsNullOrEmpty(response))
            {
                Log.Warning("PlexLogic", "Empty scheduling response.");
                return null;
            }

            List<SchedulingJobDto> jobs = ParseSchedulingJobs(response);
            SchedulingJobDto match = jobs.FirstOrDefault(s => s.jobNumber == jobNo);
            if (match == null)
                Log.Warning("PlexLogic", "No scheduling entry matched job number '" + jobNo + "'.");

            return match;
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "FetchSchedulingJob failed: " + ex.Message);
            return null;
        }
    }

    // Reads the Connect API key from the 'apiKey' variable on this NetLogic.
    private string GetConnectApiKey()
    {
        IUAVariable apiKeyVar = LogicObject.GetVariable("apiKey");
        if (apiKeyVar == null)
        {
            Log.Error("PlexLogic", "Variable 'apiKey' not found on LogicObject.");
            return null;
        }

        return apiKeyVar.Value != null ? apiKeyVar.Value.Value.ToString() : null;
    }

    // =====================================================================
    //  Minimal JSON helpers (no System.Text.Json / Newtonsoft)
    // =====================================================================

    // Escapes a string for embedding in a JSON body.
    private static string JsonString(string value)
    {
        if (value == null) return "null";
        StringBuilder sb = new StringBuilder();
        sb.Append('"');
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < ' ')
                        sb.Append("\\u" + ((int)ch).ToString("x4"));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    // Parses the datasource response and extracts the column names and the
    // first data row. Returns false if the structure isn't found.
    private static bool TryParseFirstTableRow(string json, out List<string> columns, out List<string> firstRow)
    {
        columns = null;
        firstRow = null;

        JsonValue root = JsonValue.Parse(json);
        if (root == null) return false;

        JsonValue tables = root.GetProperty("tables");
        if (tables == null || !tables.IsArray || tables.Items.Count == 0)
            return false;

        JsonValue table = tables.Items[0];

        JsonValue columnsVal = table.GetProperty("columns");
        JsonValue rowsVal = table.GetProperty("rows");
        if (columnsVal == null || !columnsVal.IsArray) return false;
        if (rowsVal == null || !rowsVal.IsArray || rowsVal.Items.Count == 0) return false;

        columns = new List<string>();
        foreach (JsonValue col in columnsVal.Items)
            columns.Add(col.AsString());

        firstRow = new List<string>();
        JsonValue row = rowsVal.Items[0];
        if (!row.IsArray) return false;
        foreach (JsonValue cell in row.Items)
            firstRow.Add(cell.AsString());

        return true;
    }

    // Parses a JSON array of scheduling job objects into DTOs.
    private static List<SchedulingJobDto> ParseSchedulingJobs(string json)
    {
        List<SchedulingJobDto> result = new List<SchedulingJobDto>();

        JsonValue root = JsonValue.Parse(json);
        if (root == null || !root.IsArray) return result;

        foreach (JsonValue item in root.Items)
        {
            if (!item.IsObject) continue;
            SchedulingJobDto dto = new SchedulingJobDto();
            dto.id = ValueOrNull(item, "id");
            dto.jobNumber = ValueOrNull(item, "jobNumber");
            dto.partNumber = ValueOrNull(item, "partNumber");
            dto.dueDate = ValueOrNull(item, "dueDate");
            dto.jobStatus = ValueOrNull(item, "jobStatus");
            dto.priority = ValueOrNull(item, "priority");
            dto.jobType = ValueOrNull(item, "jobType");
            dto.quantity = ValueOrNull(item, "quantity");
            dto.quantityCompleted = ValueOrNull(item, "quantityCompleted");
            result.Add(dto);
        }

        return result;
    }

    private static string ValueOrNull(JsonValue obj, string name)
    {
        JsonValue v = obj.GetProperty(name);
        return v != null ? v.AsString() : null;
    }

    // =====================================================================
    //  HTTP helpers  (HttpRequestMessage + async SendAsync, per FT example)
    // =====================================================================

    private string DatasourcePost(string endpoint, string jsonBody, string label = null)
    {
        string tag = label != null ? label : "Datasource";

        try
        {
            if (LogPlexPayloads)
                Log.Info("PlexLogic", tag + " REQUEST -> POST " + DatasourceURL + endpoint + " " + jsonBody);

            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, DatasourceURL + endpoint);
            StringContent content = new StringContent(jsonBody, SysEncoding.UTF8, "application/json");
            // Plex datasources can reject the "; charset=utf-8" suffix; clear it.
            content.Headers.ContentType.CharSet = "";
            request.Content = content;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("Authorization", BasicAuthHeader);

            HttpResult httpResponse = SendAsync(request).GetAwaiter().GetResult();

            if (httpResponse.Code < 200 || httpResponse.Code >= 300)
            {
                Log.Error("PlexLogic", tag + " RESPONSE <- " + httpResponse.Code + " " +
                                       Truncate(httpResponse.Payload, 800));
                return null;
            }

            if (LogPlexPayloads)
                Log.Info("PlexLogic", tag + " RESPONSE <- " + httpResponse.Code + " " +
                                      Truncate(httpResponse.Payload, 800));

            return httpResponse.Payload;
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", tag + " POST " + endpoint + " failed: " + ex.Message);
            return null;
        }
    }

    // Keeps a long datasource payload from flooding the log.
    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "... [" + s.Length + " chars]";
    }

    private string ConnectGet(string apiKey, string endpoint)
    {
        try
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, ConnectURL + endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("X-Plex-Connect-Api-Key", apiKey);

            HttpResult httpResponse = SendAsync(request).GetAwaiter().GetResult();

            if (httpResponse.Code < 200 || httpResponse.Code >= 300)
            {
                Log.Error("PlexLogic", "Connect GET " + endpoint + " returned " + httpResponse.Code + ": " + httpResponse.Payload);
                return null;
            }

            return httpResponse.Payload;
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "Connect GET " + endpoint + " failed: " + ex.Message);
            return null;
        }
    }

    // POST to the Connect API with the API-key header (status updates).
    private string ConnectPost(string apiKey, string endpoint, string jsonBody)
    {
        try
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, ConnectURL + endpoint);
            StringContent content = new StringContent(jsonBody, SysEncoding.UTF8, "application/json");
            request.Content = content;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("X-Plex-Connect-Api-Key", apiKey);

            HttpResult httpResponse = SendAsync(request).GetAwaiter().GetResult();

            if (httpResponse.Code < 200 || httpResponse.Code >= 300)
            {
                Log.Error("PlexLogic", "Connect POST " + endpoint + " returned " + httpResponse.Code + ": " + httpResponse.Payload);
                return null;
            }

            // Some Connect endpoints return an empty body on success; return
            // a non-null marker so callers treat it as OK.
            return httpResponse.Payload != null ? httpResponse.Payload : string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "Connect POST " + endpoint + " failed: " + ex.Message);
            return null;
        }
    }

    private async Task<HttpResult> SendAsync(HttpRequestMessage request)
    {
        HttpResponseMessage response = await _httpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        int code = (int)response.StatusCode;
        response.Dispose();
        return new HttpResult(body, code);
    }

    // =====================================================================
    //  Helper types
    // =====================================================================

    private struct HttpResult
    {
        public HttpResult(string payload, int code)
        {
            Payload = payload;
            Code = code;
        }

        public string Payload { get; private set; }
        public int Code { get; private set; }
    }

    // The four keys Scrap_Add needs to accept a transaction.
    private sealed class ScrapContext
    {
        public ScrapContext(int jobKey, int partKey, int partOperationKey, int workcenterKey)
        {
            JobKey = jobKey;
            PartKey = partKey;
            PartOperationKey = partOperationKey;
            WorkcenterKey = workcenterKey;
        }

        public int JobKey { get; private set; }
        public int PartKey { get; private set; }
        public int PartOperationKey { get; private set; }
        public int WorkcenterKey { get; private set; }

        public bool SameAs(ScrapContext other)
        {
            if (other == null) return false;
            return JobKey == other.JobKey
                && PartKey == other.PartKey
                && PartOperationKey == other.PartOperationKey
                && WorkcenterKey == other.WorkcenterKey;
        }
    }

    // Holds one datasource row plus its column headers, with name-based lookup.
    private sealed class JobRow
    {
        private readonly List<string> _columns;
        private readonly List<string> _cells;

        public JobRow(List<string> columns, List<string> cells)
        {
            _columns = columns;
            _cells = cells;
        }

        public string GetValue(string colName)
        {
            int idx = _columns.IndexOf(colName);
            if (idx < 0 || idx >= _cells.Count) return "-";
            string val = _cells[idx];
            return string.IsNullOrEmpty(val) ? "-" : val;
        }
    }

    private class SchedulingJobDto
    {
        public string id { get; set; }
        public string jobNumber { get; set; }
        public string partNumber { get; set; }
        public string dueDate { get; set; }
        public string jobStatus { get; set; }
        public string priority { get; set; }
        public string jobType { get; set; }
        public string quantity { get; set; }
        public string quantityCompleted { get; set; }
    }

    // ---------------------------------------------------------------------
    //  Tiny recursive-descent JSON parser (objects, arrays, strings,
    //  numbers, bool, null). Enough for the Plex response shapes.
    // ---------------------------------------------------------------------
    private sealed class JsonValue
    {
        public enum Kind { Object, Array, String, Number, Bool, Null }

        public Kind Type { get; private set; }
        public string StringValue { get; private set; }
        public List<JsonValue> Items { get; private set; }
        public Dictionary<string, JsonValue> Members { get; private set; }

        public bool IsObject { get { return Type == Kind.Object; } }
        public bool IsArray { get { return Type == Kind.Array; } }

        public JsonValue GetProperty(string name)
        {
            if (Type != Kind.Object || Members == null) return null;
            JsonValue v;
            return Members.TryGetValue(name, out v) ? v : null;
        }

        // Returns the scalar as a string; "-" for null so the UI shows a dash.
        public string AsString()
        {
            switch (Type)
            {
                case Kind.String:
                case Kind.Number:
                case Kind.Bool:
                    return StringValue;
                default:
                    return "-";
            }
        }

        public static JsonValue Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int pos = 0;
            JsonValue result = ParseValue(text, ref pos);
            return result;
        }

        private static JsonValue ParseValue(string s, ref int pos)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) return null;

            char c = s[pos];
            if (c == '{') return ParseObject(s, ref pos);
            if (c == '[') return ParseArray(s, ref pos);
            if (c == '"') return new JsonValue { Type = Kind.String, StringValue = ParseString(s, ref pos) };
            if (c == 't' || c == 'f') return ParseBool(s, ref pos);
            if (c == 'n') return ParseNull(s, ref pos);
            return ParseNumber(s, ref pos);
        }

        private static JsonValue ParseObject(string s, ref int pos)
        {
            JsonValue obj = new JsonValue { Type = Kind.Object, Members = new Dictionary<string, JsonValue>() };
            pos++; // consume '{'
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return obj; }

            while (pos < s.Length)
            {
                SkipWhitespace(s, ref pos);
                string key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos < s.Length && s[pos] == ':') pos++;
                JsonValue val = ParseValue(s, ref pos);
                obj.Members[key] = val;
                SkipWhitespace(s, ref pos);
                if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                if (pos < s.Length && s[pos] == '}') { pos++; break; }
                break;
            }
            return obj;
        }

        private static JsonValue ParseArray(string s, ref int pos)
        {
            JsonValue arr = new JsonValue { Type = Kind.Array, Items = new List<JsonValue>() };
            pos++; // consume '['
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return arr; }

            while (pos < s.Length)
            {
                JsonValue val = ParseValue(s, ref pos);
                arr.Items.Add(val);
                SkipWhitespace(s, ref pos);
                if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                if (pos < s.Length && s[pos] == ']') { pos++; break; }
                break;
            }
            return arr;
        }

        private static string ParseString(string s, ref int pos)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length || s[pos] != '"') return null;
            pos++; // consume opening quote
            StringBuilder sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"') break;
                if (c == '\\' && pos < s.Length)
                {
                    char esc = s[pos++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (pos + 4 <= s.Length)
                            {
                                string hex = s.Substring(pos, 4);
                                int code;
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out code))
                                    sb.Append((char)code);
                                pos += 4;
                            }
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static JsonValue ParseNumber(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length)
            {
                char c = s[pos];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                    pos++;
                else
                    break;
            }
            string num = s.Substring(start, pos - start);
            return new JsonValue { Type = Kind.Number, StringValue = num };
        }

        private static JsonValue ParseBool(string s, ref int pos)
        {
            if (pos + 4 <= s.Length && s.Substring(pos, 4) == "true")
            {
                pos += 4;
                return new JsonValue { Type = Kind.Bool, StringValue = "true" };
            }
            if (pos + 5 <= s.Length && s.Substring(pos, 5) == "false")
            {
                pos += 5;
                return new JsonValue { Type = Kind.Bool, StringValue = "false" };
            }
            pos++;
            return new JsonValue { Type = Kind.Null };
        }

        private static JsonValue ParseNull(string s, ref int pos)
        {
            if (pos + 4 <= s.Length && s.Substring(pos, 4) == "null")
                pos += 4;
            else
                pos++;
            return new JsonValue { Type = Kind.Null };
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos]))
                pos++;
        }
    }
}
