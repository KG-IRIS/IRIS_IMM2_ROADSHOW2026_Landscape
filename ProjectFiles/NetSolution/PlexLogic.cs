#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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

public class PlexLogic : BaseNetLogic
{
    // ---- Plex endpoints / auth ----
    // NOTE: this points at PRODUCTION. All of the scrap payload testing was done
    // against https://kendall-disc.test.on.plex.com. Point this at the test host
    // while you validate the scrap flow, then switch back.
    private const string DatasourceURL = "https://kendall-disc.on.plex.com";
    private const string DataSourceId = "10638";
    private const string BasicAuthHeader = "Basic SXJpc0lBRGlzY3JldGVXc0BwbGV4LmNvbTphMDc5NjkyLWIzMTctNA==";
    private const string ConnectURL = "https://connect.plex.com";

    // ---- UI / dialog config ----
    private const string IrisDialogPath = "UI/Dialogs/PlexIrisDialog";
    private const string KendallDialogPath = "UI/Dialogs/PlexKendallDialog";
    private const int DialogTimeoutMs = 10000;

    // ---- Model object the dialog binds to (single alias) ----
    private const string PlexJobPath = "Model/Plex/PlexJob1";

    // ---- Production recording (driven by the GoodCount PLC tag) ----
    // Production is NO LONGER posted on a timer. Every increment of GoodCount
    // posts that delta to Record_Production, so Plex mirrors the shot counter
    // instead of a made-up "1 every 30 seconds".
    private const string RecordDataSourceId = "20446";
    private const string RecordPlcName = "s-2";

    // Writes every datasource request body and response to the FactoryTalk log.
    // Leave on while proving the flow out; turn off once it's trusted.
    private const bool LogPlexPayloads = true;

    // ---- Scrap recording (Scrap_Add) ----
    // Verified payload shape:
    //   { "inputs": { "Job_Key", "Workcenter_Key", "Part_Key",
    //                 "Part_Operation_Key", "Quantity",
    //                 "Scrap_Reason", "Scrap_Date" } }
    // Returns { "outputs": { "Scrap_Key": <int> } } on success.
    private const string ScrapDataSourceId = "10363";

    // Must match a row in the "Scrap Reason" setup table (part.dbo.scrap_reason).
    // "Cracks" is verified working. Do NOT put the Scrap_Reason_Key (382/386)
    // here - this field is the varchar name, not the key.
    private const string ScrapReasonProgram = "Cracks";
    private const string ScrapReasonManual = "Cracks";

    // Quantity added per manual button press.
    private const int ManualScrapQuantity = 1;

    // ScrapCountManual is a momentary pushbutton: the PLC drives it true while
    // held and false on release, so this logic never writes to it. Mechanical
    // contacts can bounce, though, so a second rising edge inside this window
    // is treated as the same press.
    private const int ManualScrapDebounceMs = 500;

    private const string BallRequestCode = "100";
    private const int BallRequestPulseMs = 60000;

    // ---- Controller tag polling ----
    // GoodCount, ScrapCountProgram, ScrapCountManual, Production, Idle and Off
    // all live on the EtherNet/IP driver. Nothing on screen is bound to them,
    // so without a RemoteVariableSynchronizer their cached values never refresh
    // and VariableChange never fires. One synchronizer covers all six.
    //
    // 500ms is a compromise: fast enough to catch a momentary button press,
    // slow enough not to hammer the controller with six tags. Raise it if the
    // driver complains; see the note on ScrapCountManual below.
    private const int TagPollIntervalMs = 500;

    // Refuse to post a counter jump larger than this in a single update. A jump
    // that big means a bad baseline, a counter reset, or a garbage read - not
    // that the machine really made 5000 parts between two polls. Posting it
    // would write junk into Plex that has to be backed out by hand.
    private const int MaxCounterDelta = 500;

    // ---- Workcenter status IDs (Connect API) ----
    private const string StatusIdle = "5ab64e92-48e6-4ef7-9ea1-59f32c5ecd9e";
    private const string StatusProduction = "41f1c708-f393-4dac-a3f8-fa582d42ab9b";
    private const string StatusOff = "0e5b2fee-aeb9-45c4-9e68-e6633605e939";

    // ---- Workcenter UUIDs (Connect API) ----
    private const string WorkcenterIdMolding1 = "2505052f-24cf-453d-aa1a-8bc661bd105e";
    // TODO: fill in the Molding2 workcenter UUID when you have it.
    private const string WorkcenterIdMolding2 = "";

    // ---- Operator clock-in target ----
    // Badge scans always clock into this workcenter, regardless of the
    // job that happens to be active.
    private const string ClockInWorkcenterId = WorkcenterIdMolding1;
    private const string ClockInWorkcenterName = "Molding1";

    // Required by the clock-in endpoint; Plex returns
    // REQUEST_VALIDATION_FAILED without it.
    private const string ClockInCostSubTypeId = "e4b36350-f977-41bc-bcf1-570e7c171795";

    // ---- Operator badge scans -> Plex accountId ----
    // The scanner keeps digits only, so each operator needs a numeric badge
    // code. Adjust the badge numbers below to whatever is printed on the
    // actual badges. Keep them distinct from job numbers ("8", "9") and the
    // ball-request code ("100").
    private static readonly Dictionary<string, string> OperatorBadgeToAccountId =
        new Dictionary<string, string>
    {
        { "201", "4e7a7693-fb3b-420b-8ec1-a428f9ecb1c1" }, // Mike Stephens
        { "202", "bd007967-9fa1-4c48-a19d-2a736886800a" }, // Bob Slawson
        { "203", "63a47e9f-a208-4fa4-b312-5884a942c660" }, // Michael Lynch
        { "204", "685a5cd4-2a2f-45dd-89b2-cdbe437d1d9d" }, // Bruce Klumpp
        { "205", "82a19553-19a4-45b4-8818-741916727fd1" }, // Hayden Hiller
        { "206", "a0ed7410-0907-49f8-a5c7-13db97395d86" }, // Darren Ash
    };

    // accountId -> display name, used when Plex doesn't echo the roster back.
    private static readonly Dictionary<string, string> OperatorAccountToName =
        new Dictionary<string, string>
    {
        { "4e7a7693-fb3b-420b-8ec1-a428f9ecb1c1", "Mike Stephens" },
        { "bd007967-9fa1-4c48-a19d-2a736886800a", "Bob Slawson" },
        { "63a47e9f-a208-4fa4-b312-5884a942c660", "Michael Lynch" },
        { "685a5cd4-2a2f-45dd-89b2-cdbe437d1d9d", "Bruce Klumpp" },
        { "82a19553-19a4-45b4-8818-741916727fd1", "Hayden Hiller" },
        { "a0ed7410-0907-49f8-a5c7-13db97395d86", "Darren Ash" },
    };

    // Optional HMI binding for the clocked-in operator's name.
    private const string OperatorFolderPath = "Model/Plex/Operator";

    // ---- Scrap key fallbacks per scanned job number ----
    // These are only used when the job datasource doesn't hand back the keys
    // (see TryBuildScrapContextFromRow). Job_Key in particular is specific to
    // ONE job instance - when Plex closes job 8 and opens a new one, this value
    // goes stale and Scrap_Add will fail on FK_Scrap_Job. Prefer the dynamic
    // lookup; treat this table as a demo-only fallback.
    private static readonly Dictionary<string, ScrapContext> JobScrapFallback =
        new Dictionary<string, ScrapContext>
    {
        { "8", new ScrapContext(10694932, 10445858, 65305216, 85866) }, // GOLFBALL-01 / Mold (pcs) / Molding1
        // TODO: job "9" (Molding2, WC 85954) - need Job_Key, Part_Key and
        // Part_Operation_Key. Get Part_Operation_Key from the Part Operation
        // screen URL (PartOperationKey=...), same way job 8 was found.
    };

    // ---- HTTP ----
    private static readonly HttpClient _httpClient = new HttpClient();

    // Keeps the controller tags refreshed even with nothing bound on screen.
    private RemoteVariableSynchronizer _tagSynchronizer;

    private IUAVariable _barcodeVariable;
    private bool _processing = false;
    private DelayedTask _dialogCloseTask;
    // Active recording target. -1 means "nothing scanned yet, don't record".
    private int _activeWorkcenterKey = -1;
    private string _activeJobNo = null;
    // Last operator clocked in via badge scan; sent with status updates.
    private string _activeAccountId = null;
    private readonly object _recordLock = new object();

    // Vision Recording
    private IUAVariable _snapreqVariable;
    private DelayedTask _snapreqResetTask;

    private IUAVariable _reqIrisBallVariable;
    private IUAVariable _reqKendallBallVariable;
    private DelayedTask _ballResetTask;

    // Workcenter status monitoring (booleans from the PLC / model)
    private IUAVariable _productionVariable;
    private IUAVariable _idleVariable;
    private IUAVariable _offVariable;

    // ---- Production / scrap counter state ----
    private IUAVariable _goodCountVariable;           // Int32, cumulative PLC shot counter
    private IUAVariable _scrapCountProgramVariable;   // Int32, cumulative PLC reject counter
    private IUAVariable _scrapCountManualVariable;    // Boolean, operator pushbutton
    private readonly object _scrapLock = new object();
    // Last value seen on the PLC shot counter. -1 = not yet baselined.
    private int _lastGoodCount = -1;
    // Last value seen on the PLC reject counter. -1 = not yet baselined.
    private int _lastScrapCount = -1;
    // Edge tracking for the momentary scrap button: only a false->true
    // transition counts as a press.
    private bool _lastManualScrapState = false;
    // When the last accepted press happened, for bounce rejection.
    private DateTime _lastManualScrapUtc = DateTime.MinValue;
    // Keys used for the next Scrap_Add call; set when a job is scanned.
    private ScrapContext _activeScrapContext = null;

    public override void Start()
    {
        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        Log.Info("PlexLogic", "Start() called on '" + LogicObject.BrowseName + "'.");

        // Subscribe to barcode changes so a new scan triggers the flow.
        _barcodeVariable = LogicObject.GetVariable("BarcodeReading");
        if (_barcodeVariable == null)
        {
            Log.Error("PlexLogic", "Variable 'BarcodeReading' not found on LogicObject '" + LogicObject.BrowseName + "'.");
            return;
        }

        string initial = _barcodeVariable.Value != null ? _barcodeVariable.Value.Value.ToString() : "(null)";
        Log.Info("PlexLogic", "Subscribed to BarcodeReading. Initial value: '" + initial + "'.");

        _barcodeVariable.VariableChange += BarcodeVariable_VariableChange;

        // Production is recorded from the GoodCount tag (subscribed further
        // down), not on a timer.

        _snapreqVariable = LogicObject.GetVariable("snapreq");
        if (_snapreqVariable == null)
            Log.Error("PlexLogic", "Variable 'snapreq' not found on LogicObject.");

        _reqIrisBallVariable = LogicObject.GetVariable("reqIrisBall");
        if (_reqIrisBallVariable == null)
            Log.Error("PlexLogic", "Variable 'reqIrisBall' not found on LogicObject.");

        _reqKendallBallVariable = LogicObject.GetVariable("reqKendallBall");
        if (_reqKendallBallVariable == null)
            Log.Error("PlexLogic", "Variable 'reqKendallBall' not found on LogicObject.");

        // ---- Workcenter status booleans ----
        _productionVariable = LogicObject.GetVariable("Production");
        if (_productionVariable == null)
            Log.Error("PlexLogic", "Variable 'Production' not found on LogicObject.");
        else
            _productionVariable.VariableChange += ProductionVariable_VariableChange;

        _idleVariable = LogicObject.GetVariable("Idle");
        if (_idleVariable == null)
            Log.Error("PlexLogic", "Variable 'Idle' not found on LogicObject.");
        else
            _idleVariable.VariableChange += IdleVariable_VariableChange;

        _offVariable = LogicObject.GetVariable("Off");
        if (_offVariable == null)
            Log.Error("PlexLogic", "Variable 'Off' not found on LogicObject.");
        else
            _offVariable.VariableChange += OffVariable_VariableChange;

        // ---- Production source ----
        _goodCountVariable = LogicObject.GetVariable("GoodCount");
        if (_goodCountVariable == null)
        {
            Log.Error("PlexLogic", "Variable 'GoodCount' not found on LogicObject; production will not be recorded.");
        }
        else
        {
            _goodCountVariable.VariableChange += GoodCountVariable_VariableChange;
            Log.Info("PlexLogic", "Subscribed to GoodCount for production recording.");
        }

        // ---- Scrap sources ----
        _scrapCountProgramVariable = LogicObject.GetVariable("ScrapCountProgram");
        if (_scrapCountProgramVariable == null)
        {
            Log.Error("PlexLogic", "Variable 'ScrapCountProgram' not found on LogicObject.");
        }
        else
        {
            _scrapCountProgramVariable.VariableChange += ScrapCountProgramVariable_VariableChange;
        }

        _scrapCountManualVariable = LogicObject.GetVariable("ScrapCountManual");
        if (_scrapCountManualVariable == null)
        {
            Log.Error("PlexLogic", "Variable 'ScrapCountManual' not found on LogicObject.");
        }
        else
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
        lock (_scrapLock)
        {
            _lastGoodCount = -1;
            _lastScrapCount = -1;
        }

        // Nothing on screen is bound to these tags, so start polling them.
        SetupTagSynchronizer();
    }

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

    public override void Stop()
    {
        // Stop polling first so no change events arrive mid-teardown.
        if (_tagSynchronizer != null)
        {
            _tagSynchronizer.Dispose();
            _tagSynchronizer = null;
        }

        if (_barcodeVariable != null)
            _barcodeVariable.VariableChange -= BarcodeVariable_VariableChange;

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

        if (_dialogCloseTask != null)
        {
            _dialogCloseTask.Dispose();
            _dialogCloseTask = null;
        }

        if (_snapreqResetTask != null)
        {
            _snapreqResetTask.Dispose();
            _snapreqResetTask = null;
        }

        if (_ballResetTask != null)
        {
            _ballResetTask.Dispose();
            _ballResetTask = null;
        }
    }

    // =====================================================================
    //  Barcode handling
    // =====================================================================

    private void BarcodeVariable_VariableChange(object sender, VariableChangeEventArgs e)
    {
        try
        {
            string raw = e.NewValue != null ? e.NewValue.Value.ToString() : null;
            Log.Info("PlexLogic", "BarcodeReading changed to: '" + (raw != null ? raw : "(null)") + "'.");

            if (string.IsNullOrWhiteSpace(raw))
                return;

            // Scanners often append control chars (CR/LF/etc). Keep digits only.
            string barcode = KeepDigits(raw);
            if (string.IsNullOrEmpty(barcode))
            {
                Log.Warning("PlexLogic", "Scan '" + raw + "' contained no digits; ignored.");
                return;
            }

            if (_processing)
                return;
            _processing = true;
            try
            {
                HandleScan(raw, barcode);
            }
            finally
            {
                _processing = false;
                // Reset the source so scanning the SAME code again is a real change.
                ResetBarcodeVariable();
            }
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "BarcodeVariable_VariableChange failed: " + ex.Message);
        }
    }

    // Keeps only 0-9 from the scanned string.
    private static string KeepDigits(string s)
    {
        if (s == null) return null;
        StringBuilder sb = new StringBuilder();
        foreach (char c in s)
            if (c >= '0' && c <= '9')
                sb.Append(c);
        return sb.ToString();
    }

    // Clears BarcodeReading so an identical next scan raises VariableChange.
    private void ResetBarcodeVariable()
    {
        try
        {
            if (_barcodeVariable != null)
                _barcodeVariable.Value = string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "ResetBarcodeVariable failed: " + ex.Message);
        }
    }

    private void HandleScan(string rawScan, string jobNo)
    {
        try
        {
            Log.Info("PlexLogic", "HandleScan started for '" + jobNo + "'.");

            // ---- Operator badge scan (checked before the digit-only paths) ----
            // A QR holding a raw accountId UUID survives here because we look at
            // the untouched scan text, not the digits-only version.
            string scannedUuid = rawScan != null ? rawScan.Trim() : null;
            if (LooksLikeUuid(scannedUuid) && IsKnownAccountId(scannedUuid))
            {
                HandleOperatorScan(scannedUuid, scannedUuid);
                return;
            }

            // Numeric badge code (201-206) mapped to an accountId.
            string accountId;
            if (OperatorBadgeToAccountId.TryGetValue(jobNo, out accountId))
            {
                HandleOperatorScan(accountId, jobNo);
                return;
            }

            // Ball-request scan: don't change the job, just pick a ball for the active job.
            if (jobNo == BallRequestCode)
            {
                HandleBallRequest();
                return;
            }

            if (jobNo == "8" || jobNo == "9")
                TriggerSnapReq();

            List<SchedulingJobDto> scheduleJobs = FetchSchedulingJobs();
            SchedulingJobDto sched = scheduleJobs.FirstOrDefault(s => s.jobNumber == jobNo);
            if (sched == null)
                Log.Warning("PlexLogic", "No scheduling job matched job number '" + jobNo + "'.");

            int workcenterKey = GetWorkcenterKeyForJob(jobNo);
            JobRow row = null;
            if (workcenterKey > 0)
                row = FetchJobRowByWorkcenterKey(workcenterKey, jobNo);

            SetActiveRecording(workcenterKey, jobNo);
            SetActiveScrapContext(jobNo, workcenterKey, row);

            IUANode plexJob = PopulatePlexJob(row, sched, jobNo);

            NodeId aliasNode = plexJob != null ? plexJob.NodeId : null;
            OpenDialog(aliasNode, jobNo);
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "HandleScan(" + jobNo + ") failed: " + ex.Message);
        }
    }

    private void HandleBallRequest()
    {
        string jobNo;
        lock (_recordLock)
        {
            jobNo = _activeJobNo;
        }

        string ball = GetPartNameForJob(jobNo);
        Log.Info("PlexLogic", "Ball request on job '" + jobNo + "' -> '" + ball + "'.");

        // Pulse the matching ball-request flag for the active job.
        TriggerBallRequest(jobNo);
    }

    private void TriggerSnapReq()
    {
        try
        {
            if (_snapreqVariable == null)
                return;

            _snapreqVariable.Value = true;
            Log.Info("PlexLogic", "snapreq set true; will reset in 10s.");

            if (_snapreqResetTask != null)
                _snapreqResetTask.Dispose();

            _snapreqResetTask = new DelayedTask(ResetSnapReq, 10000, LogicObject);
            _snapreqResetTask.Start();
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "TriggerSnapReq failed: " + ex.Message);
        }
    }

    private void TriggerBallRequest(string jobNo)
    {
        try
        {
            string j = jobNo != null ? jobNo.Trim() : null;

            IUAVariable target = null;
            if (j == "8") target = _reqIrisBallVariable;
            else if (j == "9") target = _reqKendallBallVariable;

            if (target == null)
            {
                Log.Warning("PlexLogic", "No ball-request variable for job '" + jobNo + "'.");
                return;
            }

            // Clear both first so only one flag is ever high at a time.
            ResetBallRequestVars();

            target.Value = true;
            Log.Info("PlexLogic", "Ball request flag set for job '" + jobNo + "'; will reset in " +
                      (BallRequestPulseMs / 1000) + "s.");

            if (_ballResetTask != null)
                _ballResetTask.Dispose();

            _ballResetTask = new DelayedTask(ResetBallRequest, BallRequestPulseMs, LogicObject);
            _ballResetTask.Start();
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "TriggerBallRequest failed: " + ex.Message);
        }
    }

    private void ResetBallRequest(DelayedTask task)
    {
        ResetBallRequestVars();
        Log.Info("PlexLogic", "Ball request flags reset to false.");
    }

    private void ResetBallRequestVars()
    {
        try
        {
            if (_reqIrisBallVariable != null)
                _reqIrisBallVariable.Value = false;
            if (_reqKendallBallVariable != null)
                _reqKendallBallVariable.Value = false;
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "ResetBallRequestVars failed: " + ex.Message);
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

    private static int GetWorkcenterKeyForJob(string jobNo)
    {
        string j = jobNo != null ? jobNo.Trim() : null;
        if (j == "8") return 85866;
        if (j == "9") return 85954;

        Log.Warning("PlexLogic", "No workcenter key mapping for job '" + jobNo + "'.");
        return -1;
    }

    private static string GetWorkcenterNameForJob(string jobNo)
    {
        string j = jobNo != null ? jobNo.Trim() : null;
        if (j == "8") return "Molding1";
        if (j == "9") return "Molding2";
        return "Unknown";
    }

    // Connect-API workcenter UUID for a scanned job. Falls back to the
    // clock-in workcenter when the mapping isn't filled in yet.
    private static string GetWorkcenterIdForJob(string jobNo)
    {
        string j = jobNo != null ? jobNo.Trim() : null;
        if (j == "8" && !string.IsNullOrEmpty(WorkcenterIdMolding1)) return WorkcenterIdMolding1;
        if (j == "9" && !string.IsNullOrEmpty(WorkcenterIdMolding2)) return WorkcenterIdMolding2;
        return ClockInWorkcenterId;
    }

    // =====================================================================
    //  Operator clock-in / clock-out (Connect API)
    // =====================================================================

    // True if the text has the 8-4-4-4-12 hex shape of a UUID.
    private static bool LooksLikeUuid(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length != 36) return false;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (i == 8 || i == 13 || i == 18 || i == 23)
            {
                if (c != '-') return false;
            }
            else if (!((c >= '0' && c <= '9') ||
                       (c >= 'a' && c <= 'f') ||
                       (c >= 'A' && c <= 'F')))
            {
                return false;
            }
        }
        return true;
    }

    // Only clock in accountIds we recognise, so a stray UUID scan is ignored.
    private static bool IsKnownAccountId(string accountId)
    {
        foreach (KeyValuePair<string, string> kv in OperatorBadgeToAccountId)
        {
            if (string.Equals(kv.Value, accountId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Entry point for any operator scan. Re-scanning the operator who is
    // already clocked in badges them out; any other scan swaps the operator.
    private void HandleOperatorScan(string accountId, string badge)
    {
        string current;
        lock (_recordLock)
        {
            current = _activeAccountId;
        }

        if (!string.IsNullOrEmpty(current) &&
            string.Equals(current, accountId, StringComparison.OrdinalIgnoreCase))
        {
            ClockOutOperator(badge);
            return;
        }

        ClockInOperator(accountId, badge);
    }

    // Clocks out whoever is on the workcenter, then clocks the scanned
    // operator in. Always targets ClockInWorkcenterId.
    private void ClockInOperator(string accountId, string badge)
    {
        try
        {
            string apiKey = GetConnectApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Log.Warning("PlexLogic", "apiKey is empty; cannot clock in operator.");
                return;
            }

            // Clear the station first so only one operator is ever clocked in.
            string previous;
            lock (_recordLock) { previous = _activeAccountId; }
            ClockOutAccount(apiKey, previous);

            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"accountId\":").Append(JsonString(accountId));
            sb.Append(",\"costSubTypeId\":").Append(JsonString(ClockInCostSubTypeId));
            sb.Append("}");

            string endpoint = "/production/v1/control/workcenters/" + ClockInWorkcenterId + "/operators/clockin";

            string clockInResponse = ConnectPost(apiKey, endpoint, sb.ToString());
            if (clockInResponse == null)
            {
                Log.Warning("PlexLogic", "Clock-in failed for badge '" + badge + "' on " + ClockInWorkcenterName + ".");
                lock (_recordLock) { _activeAccountId = null; }
                return;
            }

            lock (_recordLock) { _activeAccountId = accountId; }

            // The clock-in response carries the workcenter's operator roster;
            // show the first entry's name on the HMI.
            string name = ParseFirstOperatorName(clockInResponse);
            SetOperatorDisplay(!string.IsNullOrEmpty(name) ? name : GetOperatorNameForAccount(accountId));

            Log.Info("PlexLogic", "Operator '" + badge + "' clocked in to " + ClockInWorkcenterName + ".");
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "ClockInOperator failed: " + ex.Message);
        }
    }

    // Badges the current operator out and leaves the workcenter unmanned.
    private void ClockOutOperator(string badge)
    {
        try
        {
            string apiKey = GetConnectApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Log.Warning("PlexLogic", "apiKey is empty; cannot clock out operator.");
                return;
            }

            string current;
            lock (_recordLock) { current = _activeAccountId; }

            if (ClockOutAccount(apiKey, current))
            {
                lock (_recordLock) { _activeAccountId = null; }
                SetOperatorDisplay("-");
                Log.Info("PlexLogic", "Operator '" + badge + "' clocked out of " + ClockInWorkcenterName + ".");
            }
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "ClockOutOperator failed: " + ex.Message);
        }
    }

    // Clocks one specific operator off the workcenter. An empty body makes
    // Plex return a 500, so the accountId always goes in the body.
    private bool ClockOutAccount(string apiKey, string accountId)
    {
        if (string.IsNullOrEmpty(accountId))
            return true; // nobody recorded as clocked in; nothing to do

        string endpoint = "/production/v1/control/workcenters/" + ClockInWorkcenterId + "/operators/clockout";
        string body = "{\"accountId\":" + JsonString(accountId) + "}";

        if (ConnectPost(apiKey, endpoint, body) == null)
        {
            Log.Warning("PlexLogic", "Clock-out request failed on " + ClockInWorkcenterName + ".");
            return false;
        }

        Log.Info("PlexLogic", "Cleared operator from " + ClockInWorkcenterName + ".");
        return true;
    }

    // Pulls operators[0].employeeName out of a clock-in response.
    private static string ParseFirstOperatorName(string json)
    {
        try
        {
            JsonValue root = JsonValue.Parse(json);
            if (root == null || !root.IsObject) return null;

            JsonValue operators = root.GetProperty("operators");
            if (operators == null || !operators.IsArray || operators.Items.Count == 0)
                return null;

            JsonValue first = operators.Items[0];
            if (!first.IsObject) return null;

            JsonValue name = first.GetProperty("employeeName");
            return name != null ? name.AsString() : null;
        }
        catch
        {
            return null;
        }
    }

    // Fallback name from the local badge table if Plex doesn't echo one back.
    private static string GetOperatorNameForAccount(string accountId)
    {
        foreach (KeyValuePair<string, string> kv in OperatorAccountToName)
        {
            if (string.Equals(kv.Key, accountId, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return "-";
    }

    // Writes the current operator name to Model/Plex/Operator.User if present.
    private void SetOperatorDisplay(string name)
    {
        try
        {
            IUANode folder = Project.Current.Get(OperatorFolderPath);
            if (folder == null)
                return; // optional UI binding; ignore if the folder isn't there

            IUAVariable variable = folder.GetVariable("User");
            if (variable == null)
            {
                Log.Warning("PlexLogic", "Variable 'User' not found in '" + OperatorFolderPath + "'.");
                return;
            }

            variable.Value = name != null ? name : "-";
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "SetOperatorDisplay failed: " + ex.Message);
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

    // True only on a rising edge (value changed to true).
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

    // Posts a status update for the currently active workcenter.
    private void SetWorkcenterStatus(string statusId, string statusName)
    {
        try
        {
            string accountId;
            string jobNo;
            lock (_recordLock)
            {
                accountId = _activeAccountId;
                jobNo = _activeJobNo;
            }

            // Route the status to the workcenter for the job that's actually
            // running. Falls back to the clock-in workcenter when the UUID for
            // that job isn't mapped yet (e.g. Molding2).
            string workcenterId = GetWorkcenterIdForJob(jobNo);

            string apiKey = GetConnectApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Log.Warning("PlexLogic", "apiKey is empty; cannot set workcenter status.");
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"workcenterStatusId\":").Append(JsonString(statusId));
            if (!string.IsNullOrEmpty(accountId))
                sb.Append(",\"accountId\":").Append(JsonString(accountId));
            sb.Append("}");

            string endpoint = "/production/v1/control/workcenters/" + workcenterId + "/status";

            string response = ConnectPost(apiKey, endpoint, sb.ToString());
            if (response == null)
            {
                Log.Warning("PlexLogic", "Status '" + statusName + "' update failed for workcenter '" + workcenterId + "'.");
                return;
            }

            Log.Info("PlexLogic", "Workcenter '" + workcenterId + "' status set to '" + statusName + "'.");
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "SetWorkcenterStatus(" + statusName + ") failed: " + ex.Message);
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
            int newCount;
            if (e.NewValue == null || e.NewValue.Value == null)
                return;

            try { newCount = Convert.ToInt32(e.NewValue.Value); }
            catch { return; }

            int delta;
            lock (_scrapLock)
            {
                if (_lastScrapCount < 0)
                {
                    _lastScrapCount = newCount;
                    Log.Info("PlexLogic", "Scrap counter baselined at " + newCount + " (first change).");
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
            Log.Error("PlexLogic", "ScrapCountProgramVariable_VariableChange failed: " + ex.Message);
        }
    }

    // Operator scrap pushbutton (momentary). The PLC drives the tag true while
    // the button is held and false when released, so this only watches for the
    // false->true edge and never writes to the tag. One press = one part,
    // however long it's held down.
    private void ScrapCountManualVariable_VariableChange(object sender, VariableChangeEventArgs e)
    {
        try
        {
            bool now = IsTrue(e);
            bool pressed;
            bool bounced = false;

            lock (_scrapLock)
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
            Log.Error("PlexLogic", "ScrapCountManualVariable_VariableChange failed: " + ex.Message);
        }
    }

    // Posts one Scrap_Add transaction for the active job.
    private void RecordScrap(int quantity, string scrapReason, string source)
    {
        if (quantity <= 0)
            return;

        ScrapContext ctx;
        lock (_scrapLock) { ctx = _activeScrapContext; }

        if (ctx == null)
        {
            Log.Warning("PlexLogic", "Scrap from " + source + " (qty " + quantity +
                                     ") ignored: no job scanned, so no keys to post against.");
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
                                     ", reason '" + scrapReason + "', WC " + ctx.WorkcenterKey + ").");
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

    // Works out which keys future scrap postings should use for this job, and
    // rebaselines the PLC counter so scrap from the previous job isn't carried
    // over onto the new one.
    private void SetActiveScrapContext(string jobNo, int workcenterKey, JobRow row)
    {
        ScrapContext ctx = TryBuildScrapContextFromRow(row, workcenterKey);

        if (ctx == null)
        {
            ScrapContext fallback;
            if (jobNo != null && JobScrapFallback.TryGetValue(jobNo.Trim(), out fallback))
            {
                ctx = fallback;
                Log.Info("PlexLogic", "Scrap keys for job '" + jobNo + "' taken from the local fallback table.");
            }
        }
        else
        {
            Log.Info("PlexLogic", "Scrap keys for job '" + jobNo + "' resolved from the job datasource.");
        }

        if (ctx == null)
        {
            Log.Warning("PlexLogic", "No scrap keys available for job '" + jobNo +
                                     "'; scrap will not be recorded until they're configured.");
        }

        int currentScrap;
        bool haveScrap = TryReadInt(_scrapCountProgramVariable, out currentScrap);
        int currentGood;
        bool haveGood = TryReadInt(_goodCountVariable, out currentGood);

        lock (_scrapLock)
        {
            _activeScrapContext = ctx;
            // Rebaseline on job change so the first delta after a scan isn't
            // whatever the counters accumulated under the previous job.
            _lastScrapCount = haveScrap ? currentScrap : -1;
            _lastGoodCount = haveGood ? currentGood : -1;
        }
    }

    // Pulls Job_Key / Part_Key / Part_Operation_Key from the job datasource row
    // if that datasource returns them. Returns null when any are missing, so the
    // caller can fall back to the static table.
    private static ScrapContext TryBuildScrapContextFromRow(JobRow row, int workcenterKey)
    {
        if (row == null || workcenterKey <= 0)
            return null;

        long jobKey, partKey, partOpKey;
        if (!TryParsePlexInt(row.GetValue("Job_Key"), out jobKey)) return null;
        if (!TryParsePlexInt(row.GetValue("Part_Key"), out partKey)) return null;
        if (!TryParsePlexInt(row.GetValue("Part_Operation_Key"), out partOpKey)) return null;

        if (jobKey <= 0 || partKey <= 0 || partOpKey <= 0)
            return null;

        return new ScrapContext((int)jobKey, (int)partKey, (int)partOpKey, workcenterKey);
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

    // =====================================================================
    //  Background production recording
    // =====================================================================
    private void SetActiveRecording(int workcenterKey, string jobNo)
    {
        lock (_recordLock)
        {
            if (workcenterKey <= 0)
            {
                Log.Warning("PlexLogic", "Scan '" + jobNo + "' has no workcenter; recording unchanged.");
                return;
            }

            if (_activeWorkcenterKey == workcenterKey)
            {
                Log.Info("PlexLogic", "Recording already active for job '" + jobNo + "' (WC " + workcenterKey + ").");
                return;
            }

            _activeWorkcenterKey = workcenterKey;
            _activeJobNo = jobNo;
            Log.Info("PlexLogic", "Now recording production for job '" + jobNo + "' (WC " + workcenterKey + ").");
        }
    }

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

            int delta;
            lock (_scrapLock)
            {
                if (_lastGoodCount < 0)
                {
                    _lastGoodCount = newCount;
                    Log.Info("PlexLogic", "Good counter baselined at " + newCount + " (first change).");
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

            int wcKey;
            string jobNo;
            lock (_recordLock)
            {
                wcKey = _activeWorkcenterKey;
                jobNo = _activeJobNo;
            }

            if (wcKey <= 0)
            {
                Log.Warning("PlexLogic", "GoodCount +" + delta +
                                         " ignored: no job scanned, so there's no workcenter to record against.");
                return;
            }

            RecordProduction(wcKey, jobNo, delta);
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "GoodCountVariable_VariableChange failed: " + ex.Message);
        }
    }

    // Posts one production record to Plex for the given workcenter.
    // Scrap_Quantity stays 0 here on purpose: scrap goes through Scrap_Add
    // (10363) instead, so sending it on both paths would double-count.
    private void RecordProduction(int workcenterKey, string jobNo, int quantity)
    {
        if (quantity <= 0)
            return;

        // Build body by hand (no serializer dependency):
        // {"inputs":{"Workcenter_Key":N,"PLC_Name":"s-2","Quantity":N, ...}}
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"inputs\":{");
        sb.Append("\"Workcenter_Key\":").Append(workcenterKey).Append(",");
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
                                     ", job '" + jobNo + "', WC " + workcenterKey + ".");
            return;
        }

        // Record_Production answers 200 even when it refuses the transaction,
        // so the real outcome is in outputs.Result_Error / Result_Message.
        string resultMessage;
        if (IsProductionResultError(response, out resultMessage))
        {
            Log.Warning("PlexLogic", "Production post REJECTED by Plex: qty " + quantity +
                                     ", job '" + jobNo + "', WC " + workcenterKey +
                                     " - " + (resultMessage ?? "(no message)"));
            return;
        }

        Log.Info("PlexLogic", "Production recorded: qty " + quantity +
                              ", job '" + jobNo + "', WC " + workcenterKey + ".");
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
    //  PlexJob object population (dialog binds to this object via one alias)
    // =====================================================================

    // Maps a scanned job number to the golf ball / part name.
    private static string GetPartNameForJob(string jobNo)
    {
        string j = jobNo != null ? jobNo.Trim() : null;
        if (j == "8") return "IRIS GOLF BALL";
        if (j == "9") return "KENDALL ELECTRIC GOLF BALL";

        Log.Warning("PlexLogic", "No PartName mapping for job '" + jobNo + "'.");
        return "-";
    }

    // Maps a scanned job number to its dialog type path.
    private static string GetDialogPathForJob(string jobNo)
    {
        string j = jobNo != null ? jobNo.Trim() : null;
        if (j == "8") return IrisDialogPath;
        if (j == "9") return KendallDialogPath;

        Log.Warning("PlexLogic", "No dialog mapping for job '" + jobNo + "'.");
        return null;
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

        // Try direct integer parse first.
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

    // Fills the PlexJob object's properties and returns the object node.
    private IUANode PopulatePlexJob(JobRow row, SchedulingJobDto schedule, string jobNo)
    {
        try
        {
            IUANode plexJob = GetPlexJob();
            if (plexJob == null) return null;

            // PartName comes from the job-number mapping, not Plex.
            SetObjectVar(plexJob, "PartName", GetPartNameForJob(jobNo));

            SetObjectVar(plexJob, "Job", jobNo);
            SetObjectVar(plexJob, "Workcenter", GetWorkcenterNameForJob(jobNo));

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

            // Operation also from the datasource row.
            string operation = row != null ? row.GetValue("Operation_No") : "-";
            SetObjectVar(plexJob, "Operation", operation);

            return plexJob;
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "PopulatePlexJob failed: " + ex.Message);
            return null;
        }
    }

    private IUANode GetPlexJob()
    {
        IUANode node = Project.Current.Get(PlexJobPath);
        if (node == null)
            Log.Warning("PlexLogic", "Could not find PlexJob object at '" + PlexJobPath + "'.");
        return node;
    }

    private void SetObjectVar(IUANode plexJob, string variableName, string value)
    {
        try
        {
            if (plexJob == null) return;

            IUAVariable variable = plexJob.GetVariable(variableName);
            if (variable == null)
            {
                Log.Warning("PlexLogic", "Property '" + variableName + "' not found on '" + PlexJobPath + "'.");
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
    //  Dialog handling (all native + web sessions)
    // =====================================================================

    public void OpenDialog(NodeId aliasNode = null, string jobNo = null)
    {
        try
        {
            string dialogPath = GetDialogPathForJob(jobNo);
            if (dialogPath == null)
                return;

            DialogType dialogType = Project.Current.Get(dialogPath) as DialogType;
            if (dialogType == null)
            {
                Log.Error("PlexLogic", "Could not find dialog type '" + dialogPath + "'.");
                return;
            }

            // UICommands.OpenDialog takes a NodeId[]; wrap the single alias.
            NodeId[] aliasArray = null;
            if (aliasNode != null)
                aliasArray = new NodeId[] { aliasNode };

            // Close any dialog still open from a previous scan (all sessions).
            CloseDialog();

            // ---- Native Presentation Engine (single session) ----
            IUANode nativePE = Project.Current.Get("UI/NativePresentationEngine");
            if (nativePE != null)
            {
                IUANode nativeSessions = nativePE.Get("Sessions");
                if (nativeSessions != null && nativeSessions.Children.Count > 0)
                {
                    IUANode nativeWindow = nativeSessions.Children[0].Get("UIRoot");
                    if (nativeWindow != null)
                        UICommands.OpenDialog(nativeWindow, dialogType, aliasArray);
                }
            }

            // ---- Web Presentation Engine (may have multiple sessions) ----
            IUANode webPE = Project.Current.Get("UI/WebPresentationEngine");
            if (webPE != null)
            {
                IUANode webSessions = webPE.Get("Sessions");
                if (webSessions != null)
                {
                    foreach (IUANode webSession in webSessions.Children)
                    {
                        IUANode webWindow = webSession.Get("UIRoot");
                        if (webWindow != null)
                            UICommands.OpenDialog(webWindow, dialogType, aliasArray);
                    }
                }
            }

            // Schedule an auto-close after the timeout.
            if (_dialogCloseTask != null)
                _dialogCloseTask.Dispose();
            _dialogCloseTask = new DelayedTask(AutoCloseDialog, DialogTimeoutMs, LogicObject);
            _dialogCloseTask.Start();
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "OpenDialog failed: " + ex.Message);
        }
    }

    private void AutoCloseDialog(DelayedTask task)
    {
        CloseDialog();
    }

    public void CloseDialog()
    {
        try
        {
            // Native PE (single session).
            IUANode nativePE = Project.Current.Get("UI/NativePresentationEngine");
            if (nativePE != null)
            {
                IUANode nativeSessions = nativePE.Get("Sessions");
                if (nativeSessions != null && nativeSessions.Children.Count > 0)
                    CloseDialogsOnWindow(nativeSessions.Children[0].Get("UIRoot"));
            }

            // Web PE (multiple sessions).
            IUANode webPE = Project.Current.Get("UI/WebPresentationEngine");
            if (webPE != null)
            {
                IUANode webSessions = webPE.Get("Sessions");
                if (webSessions != null)
                {
                    foreach (IUANode webSession in webSessions.Children)
                        CloseDialogsOnWindow(webSession.Get("UIRoot"));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "CloseDialog failed: " + ex.Message);
        }
    }

    private void CloseDialogsOnWindow(IUANode uiRoot)
    {
        if (uiRoot == null) return;

        foreach (Dialog dialog in uiRoot.Children.OfType<Dialog>().ToList())
            dialog.Close();
    }

    // =====================================================================
    //  Plex datasource / connect fetch  (manual JSON parsing, no external deps)
    // =====================================================================

    private JobRow FetchJobRowByWorkcenterKey(int workcenterKey, string jobNo)
    {
        try
        {
            string jsonBody = "{\"inputs\":{\"Workcenter_Key\":" + workcenterKey + "}}";

            string response = DatasourcePost("/api/datasources/" + DataSourceId + "/execute", jsonBody);
            if (string.IsNullOrEmpty(response))
            {
                Log.Warning("PlexLogic", "Job '" + jobNo + "' (WC " + workcenterKey + "): empty datasource response.");
                return null;
            }

            // Expected shape: { "tables":[ { "columns":[...], "rows":[[...],...] } ] }
            List<string> columns;
            List<string> firstRow;
            if (!TryParseFirstTableRow(response, out columns, out firstRow))
            {
                Log.Warning("PlexLogic", "Job '" + jobNo + "' (WC " + workcenterKey + "): no rows returned.");
                return null;
            }

            // One-time visibility into what this datasource actually returns -
            // if Job_Key / Part_Key / Part_Operation_Key show up here, scrap can
            // stop relying on the hardcoded fallback table.
            Log.Info("PlexLogic", "Job datasource columns: " + string.Join(", ", columns));

            return new JobRow(columns, firstRow);
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "FetchJobRowByWorkcenterKey(" + workcenterKey + ") failed: " + ex.Message);
            return null;
        }
    }

    private List<SchedulingJobDto> FetchSchedulingJobs()
    {
        try
        {
            string apiKey = GetConnectApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Log.Warning("PlexLogic", "apiKey is empty; skipping scheduling fetch.");
                return new List<SchedulingJobDto>();
            }

            string response = ConnectGet(apiKey, "/scheduling/v1/jobs");
            if (string.IsNullOrEmpty(response))
            {
                Log.Warning("PlexLogic", "Empty scheduling response.");
                return new List<SchedulingJobDto>();
            }

            return ParseSchedulingJobs(response);
        }
        catch (Exception ex)
        {
            Log.Error("PlexLogic", "FetchSchedulingJobs failed: " + ex.Message);
            return new List<SchedulingJobDto>();
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
    //  Minimal JSON parsing helpers (no System.Text.Json / Newtonsoft)
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
    // first data row using a lightweight tokenizer. Returns false if the
    // structure isn't found.
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

    // POST to the Connect API with the API-key header (clock-in, status, etc).
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
    //  numbers, bool, null). Enough for the two Plex response shapes.
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