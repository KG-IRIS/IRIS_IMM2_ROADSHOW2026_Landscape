#region Using directives
using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using FTOptix.Core;
using FTOptix.CoreBase;
using FTOptix.HMIProject;
using FTOptix.NativeUI;
using FTOptix.NetLogic;
using FTOptix.ODBCStore;
using FTOptix.Retentivity;
using FTOptix.Store;
using FTOptix.UI;
using UAManagedCore;
using FTOptix.OPCUAServer;
using FTOptix.OPCUAClient;
using OpcUa = UAManagedCore.OpcUa;
#endregion

public class bcLogic : BaseNetLogic
{
    // Network vars
    private BC_ReaderType BC_Data;
    private IPEndPoint bcScanner;
    private UdpClient client;
    string ipString;
    IPAddress ipAddress;
    Socket skt = null;
    private bool isConnected = false;
    private int packetCount = 0;

    private volatile bool cmdInProgress = false;
    private volatile int cmdRespLength = 0;
    private volatile byte[] cmdRespBytes = new byte[4096];
    private volatile int bcRecvLength = 0;
    private volatile byte[] bcRecvBytes = new byte[6144];
    private int nLengthBarcode = 0;
    private Mutex responseMutex = new Mutex();
    private Mutex commandMutex = new Mutex();
    private bool closeRxThread = false;
    private bool bHasBeenInitialized = false;
    private PeriodicTask myPeriodicTask;

    public override void Start()
    {
        // Insert code to be executed when the user-defined logic is started
        // Creates a task that runs every second without impacting the UI
        
        //Initialize Bar Code Reader Instance Data
        BC_Data = (BC_ReaderType)Owner;
        //Assign IP from instance
        ipString = BC_Data.IP_Address;

        //If Reader is not disabled start connection

        if (!BC_Data.disableBC)
        {
            reqConnection();

            myPeriodicTask = new PeriodicTask(BC_KeepAlive, 5000, LogicObject);
            myPeriodicTask.Start();
        }
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
        reqDisconnect();
    }

    private void BC_KeepAlive()

    {

        bool batreq_Success;

        Log.Info($"{isConnected}" + " Keep alive ran");

        if (isConnected)
        {
            //Used as a keepalive check
            batreq_Success = ReqSendCommand_Battery();

            if (batreq_Success)
            {
                Log.Info("This Socket OK.");
            }
            else
            {
                Log.Info("This Socket has an error.");

                //Disconnect and clear socket
                reqDisconnect();

                //Attempt reconnect
                reqConnection();
            }



        }
    }

    [ExportMethod]
    private void reqConnection()
    {

        // setup endpoint with IP address and Port
        ipAddress = IPAddress.Parse(ipString);
        bcScanner = new IPEndPoint(ipAddress, 54321);

        try
        {
            // close out receive thread in the event that it is still running
            closeRxThread = true;

            // Create TCP/IP socket
            skt = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            // Connect to the remote endpoint
            skt.Connect(bcScanner);

        }
        catch (Exception)
        {
            // if any issue occurs just return
            return;
        }

        // update status and UI state prior to starting listen thread
        UpdateConnectionState(isConnected = true);

        ReceiveBarcodeBufferClear();
        ReceiveCommandBufferClear();

        closeRxThread = false;

        Thread receiveThread = new Thread(new ThreadStart(ReceiveThread));
        receiveThread.Start();

    }

    private void reqDisconnect()
    {
        closeRxThread = true;

        // update status and UI state prior to stopping listen thread
        UpdateConnectionState(isConnected = false);

        // No longer connected, exit cleanly
        if (skt != null)
        {
            return;
        }
        skt.Shutdown(SocketShutdown.Both);
        skt.Close();
        skt = null;


    }

    // This delegate enables asynchronous calls
    delegate void UpdateConnectionCallback(bool connectionState);
    private void UpdateConnectionState(bool connectionState)
    {

        if (bHasBeenInitialized is false)
        {
            UpdateConnectionCallback d = new UpdateConnectionCallback(UpdateConnectionState);
            BC_Data.bcConnected = connectionState;
            bHasBeenInitialized = true;
        }
        else
        {
            // Update connect/disconnect button state
            if ((packetCount != 0) && !connectionState)
            {
                //Connection State Variable Management
                //for has packets and not connected
                BC_Data.bcConnected = false;

            }
            else if ((packetCount != 0) && connectionState)
            {
                //Connection State Variables
                //for has packets and is Connected
                BC_Data.bcConnected = true;
            }
            else
            {
                //Connection state for
                //No Packets and Connected or not
                BC_Data.bcConnected = isConnected;
            }
        }

        // Update UI based on connection status
        BC_Data.bcConnected = isConnected;
    }

    //Clears the receive buffer that holds command responses
    private void ReceiveCommandBufferClear()
    {
        bool bHoldsMutex = responseMutex.WaitOne(1000);

        cmdRespLength = 0;
        if (cmdRespBytes.Length != 0)
            Array.Clear(cmdRespBytes, 0, cmdRespBytes.Length);

        if (bHoldsMutex)
        {
            responseMutex.ReleaseMutex();
            bHoldsMutex = false;
        }
    }

    //Clears the receive buffer that holds bar code data
    private void ReceiveBarcodeBufferClear()
    {
        bool bHoldsMutex = responseMutex.WaitOne(1000);

        bcRecvLength = 0; // clear out receive buffer
        if (bcRecvBytes.Length != 0)
            Array.Clear(bcRecvBytes, 0, bcRecvBytes.Length);

        if (bHoldsMutex)
        {
            responseMutex.ReleaseMutex();
            bHoldsMutex = false;
        }
    }

    //Background thread that receives data being sent from the IE device
    //Bar code data or command responses are saved in the appropriate receive buffer 
    private void ReceiveThread()
    {
        int i = 0;
        int nBytesRead = 0;
        byte[] bytes = new byte[4096];
        bool bHoldsMutex = false;

        while (true)
        {
            try
            {
                // get the response from the send packet
                if (skt != null)
                    nBytesRead = skt.Receive(bytes);
                else
                    Thread.Sleep(0);

                if (skt.Connected == false)
                    UpdateConnectionState(isConnected = false);


                bHoldsMutex = responseMutex.WaitOne(1000);

                // check that we have bytes read and not overrun before acting
                if (nBytesRead > 0)
                {
                    // split response data to barcode buffer or command buffer
                    for (i = 0; i < nBytesRead; i++)
                    {
                        if (cmdInProgress && (bytes[i] == '#')) // found start byte of response, send to resp buffer
                        {
                            cmdRespBytes[cmdRespLength] = bytes[i];
                            ++cmdRespLength;
                            do
                            {
                                ++i;
                                cmdRespBytes[cmdRespLength] = bytes[i];
                                ++cmdRespLength;


                            } while ((bytes[i] != '\r') && (i < nBytesRead));

                        }
                        else // bar code rx'd
                        {
                            bcRecvBytes[bcRecvLength] = bytes[i];
                            ++bcRecvLength;
                        }
                    }


                    if (bcRecvLength > 0)
                    {
                        // get the length of the barcode
                        if (nLengthBarcode == 0)
                        {
                            nLengthBarcode = ((0xFF & bcRecvBytes[0]) << 8) | (0xFF & bcRecvBytes[1]);
                            if (nLengthBarcode < 0)
                                nLengthBarcode = 0;
                        }

                        if ((nLengthBarcode != 0) && (bcRecvLength >= nLengthBarcode))
                        {
                            ReceiveBarcode(3, nLengthBarcode - 3, bcRecvBytes[2]);
                            nLengthBarcode = 0;
                        }
                    }
                }


                if (bHoldsMutex)
                {
                    responseMutex.ReleaseMutex();
                    bHoldsMutex = false;
                }

            }

            catch (SocketException e)
            {
                Debug.WriteLine("ReceiveThreadException " + e.SocketErrorCode.ToString());

                if (bHoldsMutex)
                {
                    responseMutex.ReleaseMutex();
                    bHoldsMutex = false;
                }

                ReceiveBarcodeBufferClear();
                ReceiveCommandBufferClear();
                Thread.Sleep(10);
            }
            catch (Exception)
            {
                if (bHoldsMutex)
                {
                    responseMutex.ReleaseMutex();
                    bHoldsMutex = false;
                }
                ReceiveBarcodeBufferClear();
                ReceiveCommandBufferClear();
            }

            // Socket has been closed, exit thread
            if (closeRxThread)
                return;

            Thread.Sleep(0);
        }
    }

    //Sends bar code data to the output text box
    private void ReceiveBarcode(int offset, int length, byte type)
    {
        string nonprintstring = "";
        bool bNotPrintable;
        bool bConvertNonPrint = false;
        bool bKeyBoardWedge = false;
        int originalLength = length;
        char c;

        //First 3 characters in receive buffer are overhead / protocol bytes.
        //Bar code data starts at offset into receive buffer
        //Encoding Windows54936 = Encoding.GetEncoding(1252);
        string s = Encoding.UTF8.GetString(bcRecvBytes, offset, length);
        string keyboardWedge = "";

        bcRecvLength = 0; // clear out receive buffer
        if (bcRecvBytes.Length != 0)
            Array.Clear(bcRecvBytes, 0, bcRecvBytes.Length);

        //Insert non-printable characters into output string
        for (int i = 0; i < s.Length; i++)
        {
            bNotPrintable = true;
            switch ((ushort)s[i])
            {
                case 0x00: nonprintstring = "<<NUL>>"; break;
                case 0x01: nonprintstring = "<<SOH>>"; break;
                case 0x02: nonprintstring = "<<STX>>"; break;
                case 0x03: nonprintstring = "<<ETX>>"; break;
                case 0x04: nonprintstring = "<<EOT>>"; break;
                case 0x05: nonprintstring = "<<ENQ>>"; break;
                case 0x06: nonprintstring = "<<ACK>>"; break;
                case 0x07: nonprintstring = "<<BEL>>"; break;
                case 0x08: nonprintstring = "<<BS>>"; break;
                case 0x09: nonprintstring = "<<HT>>"; break;
                case 0x0a: nonprintstring = "<<LF>>"; break;
                case 0x0b: nonprintstring = "<<VT>>"; break;
                case 0x0c: nonprintstring = "<<FF>>"; break;
                case 0x0d: nonprintstring = "<<CR>>"; break;
                case 0x0e: nonprintstring = "<<SO>>"; break;
                case 0x0f: nonprintstring = "<<SI>>"; break;
                case 0x10: nonprintstring = "<<DLE>>"; break;
                case 0x11: nonprintstring = "<<DC1>>"; break;
                case 0x12: nonprintstring = "<<DC2>>"; break;
                case 0x13: nonprintstring = "<<DC3>>"; break;
                case 0x14: nonprintstring = "<<DC4>>"; break;
                case 0x15: nonprintstring = "<<NAK>>"; break;
                case 0x16: nonprintstring = "<<SYN>>"; break;
                case 0x17: nonprintstring = "<<ETB>>"; break;
                case 0x18: nonprintstring = "<<CAN>>"; break;
                case 0x19: nonprintstring = "<<EM>>"; break;
                case 0x1a: nonprintstring = "<<SUB>>"; break;
                case 0x1b: nonprintstring = "<<ESC>>"; break;
                case 0x1c: nonprintstring = "<<FS>>"; break;
                case 0x1d: nonprintstring = "<<GS>>"; break;
                case 0x1e: nonprintstring = "<<RS>>"; break;
                case 0x1f: nonprintstring = "<<US>>"; break;
                case 0x7f: nonprintstring = "<<DEL>>"; break;
                default: bNotPrintable = false; break;
            }

            if ((bConvertNonPrint) && bNotPrintable)
            {
                s = s.Remove(i, 1);
                s = s.Insert(i, nonprintstring);
                i = i + nonprintstring.Length - 1;
                length = s.Length;
            }
        }

        BC_Data.bcReceived = s;

        keyboardWedge = s;
        // for sendkeys, we need to enclose certain chars with braces 
        for (int i = 0; i < keyboardWedge.Length; i++)
        {
            c = keyboardWedge[i];

            switch (c)
            {
                case '+':
                case '^':
                case '%':
                case '~':
                case '(':
                case ')':
                case '[':
                case ']':
                case '{':
                case '}':
                    keyboardWedge = keyboardWedge.Remove(i, 1);
                    keyboardWedge = keyboardWedge.Insert(i, "{" + c.ToString() + "}");
                    break;
                default:
                    break;
            }
        }

        // only if the checkbox is set do we send the barcode data out as keystrokes
        if (bKeyBoardWedge == true)
        {
            //SendKeys.SendWait(keyboardWedge);
            //Replace logic or delete in future for
            //Optix migration
        }

        //this.AppendStatusLog(s);

    }

    //Formats command to be sent to IE device and calls CommandSend function to send it out
    private bool ReqSendCommand_Battery()
    {
        Regex pattern = new Regex("[#]");

        string response;
        string cmd = "BATT_INFO";
        bool success = false;

        if (cmd == "EA_ACTION")
        {
            cmd = cmd + " ";//+ txtUIF.Text;//Not used
        }

        if (isConnected)
        {
            switch (cmd)
            {
                case "REVSOFT":
                case "BOOTREV":
                case "MACADDR":
                case "IE_PROT":
                case "HI_GPN":
                case "HI_GSN":
                case "REQ_REV":
                case "MODEL":
                case "SERIAL":
                case "MFR_DATE":
                case "BATT_INFO":
                    if (!cmd.Contains(" ") && !cmd.Contains("\t"))
                    {
                        cmd += " ?"; // make it a query
                    }
                    break;
            }

            success = CommandSend(cmd, out response, true, 1000);

            response = pattern.Replace(response, ""); // remove all #

            BC_Data.bcBatteryInfo = response;

            if (success)
            {
                ParseBatteryInfo(response, out int voltage, out int soc, out int temp, out DateTime mfgDate);

                BC_Data.battVoltage = voltage;
                BC_Data.battCharge = soc;
                BC_Data.battTemp = temp;
                BC_Data.mfgDate = mfgDate;
            }
            //AppendStatusLog(response);
        }

        return success;
    }

    //Processes a response received to a command sent to the IE device
    //Returns true if command response was ok... false otherwise
    private bool CommandResponse(string cmd, ref string resp)
    {
        bool success = false;

        // command specific processing
        switch (cmd)
        {
            case "REVSOFT ?":
            case "BOOTREV ?":
            case "MACADDR ?":
            case "IE_PROT ?":
            case "HI_GPN ?":
            case "HI_GSN ?":
            case "REQ_REV ?":
            case "MODEL ?":
            case "SERIAL ?":
            case "MFR_DATE ?":
            case "BATT_INFO ?":
                resp = resp.TrimEnd(new char[] { '\r', '\0' }).TrimStart('#');
                if ((resp.Length != 0) && !resp.Equals("-"))
                    success = true;
                break;

            case "TRGON":
            case "TRGOFF":
                success = true;  //No response expected
                break;
            default:
                resp = resp.TrimEnd(new char[] { '\r', '\0' });
                if (resp.Contains("#+"))
                    success = true;
                break;
        }

        return success;
    }

    //Sends a command to IE device
    private bool CommandSend(string cmd, out string resp, bool multiResponse, int waitTimeout)
    {
        bool success;
        int nStart;
        bool bHoldsMutex = false;
        bool bCmdMutex = false;
        resp = "";

        try
        {
            bCmdMutex = commandMutex.WaitOne(1000);
            cmdInProgress = true;

            // Send command to firmware
            ReceiveCommandBufferClear();
            if (skt != null)
                skt.Send(Encoding.ASCII.GetBytes("#" + cmd + "\r"));

            if (waitTimeout == 0)
            {
                success = CommandResponse(cmd, ref resp);
            }
            else
            {
                success = false;
                nStart = System.Environment.TickCount;
                while (!success && (System.Environment.TickCount - nStart) < waitTimeout)
                {
                    if (multiResponse)
                        Thread.Sleep(50); // allow receive thread to run
                    else
                        Thread.Sleep(0); // allow receive thread to run

                    // stop rx thread so we can get a valid byte array
                    bHoldsMutex = responseMutex.WaitOne(1000);

                    // check to see if we have a valid response
                    resp = ASCIIEncoding.UTF8.GetString(cmdRespBytes, 0, cmdRespBytes.Length);
                    success = CommandResponse(cmd, ref resp);

                    if (bHoldsMutex)
                    {
                        responseMutex.ReleaseMutex();
                        bHoldsMutex = false;
                    }
                }
            }

            cmdInProgress = false;

            if (bCmdMutex)
            {
                commandMutex.ReleaseMutex();
                bCmdMutex = false;
            }
        }
        catch (SocketException e)
        {
            if (bHoldsMutex)
            {
                responseMutex.ReleaseMutex();
                bHoldsMutex = false;
            }

            cmdInProgress = false;

            if (bCmdMutex)
            {
                commandMutex.ReleaseMutex();
                bCmdMutex = false;
            }

            success = false;
            Log.Error("CommandSendException " + e.SocketErrorCode.ToString());
        }
        catch (Exception)
        {
            if (bHoldsMutex)
            {
                responseMutex.ReleaseMutex();
                bHoldsMutex = false;
            }

            cmdInProgress = false;

            if (bCmdMutex)
            {
                commandMutex.ReleaseMutex();
                bCmdMutex = false;
            }

            success = false;
        }

        return success;
    }

    private static void ParseBatteryInfo(string input, out int voltage, out int stateOfCharge, out int temperature, out DateTime mfgDate)
    {
        voltage = ExtractIntValue(input, "Batt Voltage");
        stateOfCharge = ExtractIntValue(input, "Batt State of Charge");
        temperature = ExtractIntValue(input, "Batt Temp");
        mfgDate = ExtractDateValue(input, "Batt Date of Mfg");
    }

    private static int ExtractIntValue(string input, string label)
    {
        var pattern = $@"{Regex.Escape(label)}:\s*(\d+)";
        var match = Regex.Match(input, pattern);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int result))
        {
            return result;
        }
        throw new FormatException($"Unable to parse integer value for {label}.");
    }

    private static DateTime ExtractDateValue(string input, string label)
    {
        var pattern = $@"{Regex.Escape(label)}:\s*([0-9]{{2}}[A-Z]{{3}}[0-9]{{2}})";
        var match = Regex.Match(input, pattern);
        if (match.Success && DateTime.TryParseExact(match.Groups[1].Value, "ddMMMyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
        {
            return date;
        }
        throw new FormatException($"Unable to parse date value for {label}.");
    }

}
