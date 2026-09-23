#region Using directives
using UAManagedCore;
using FTOptix.NetLogic;
using FTOptix.UI;
using FTOptix.SerialPort;
using FTOptix.Alarm;
using FTOptix.WebUI;
using FTOptix.RecipeX;
using FTOptix.Store;
using FTOptix.Report;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using FTOptix.SQLiteStore;
using FTOptix.ODBCStore;
using FTOptix.InfluxDBStore;
using FTOptix.InfluxDBStoreRemote;
using FTOptix.InfluxDBStoreLocal;
using FTOptix.EventLogger;
using FTOptix.DataLogger;
using FTOptix.MQTTClient;
using FTOptix.OPCUAServer;
using FTOptix.OPCUAClient;
#endregion

public class NetworkInterfacesEditModelReaderLogic : BaseNetLogic
{
    public NetworkInterfacesEditModelReaderLogic(IUAObject editModelNetworkInterfacesObject)
    {
        this.editModelNetworkInterfacesObject = editModelNetworkInterfacesObject;
    }

    public NetworkInterfacesEditModelReaderLogic() {}

    public IUAVariable GetDhcpVariable(string interfaceName)
    {
        var networkInterfaceObject = editModelNetworkInterfacesObject.GetObject(interfaceName);
        var variable = networkInterfaceObject.GetVariable("DHCPClientEnabled");
        if (variable == null)
            throw new CoreConfigurationException("DHCPClientEnabled variable not found");

        return variable;
    }

    public IUAVariable GetIPAddressVariable(string interfaceName)
    {
        var networkInterfaceObject = editModelNetworkInterfacesObject.GetObject(interfaceName);
        var variable = networkInterfaceObject.GetVariable("IPAddress");
        if (variable == null)
            throw new CoreConfigurationException("IPAddress variable not found");

        return variable;
    }

    public IUAVariable GetMaskVariable(string interfaceName)
    {
        var variable = GetIPAddressVariable(interfaceName).GetVariable("Mask");
        if (variable == null)
            throw new CoreConfigurationException("Mask variable not found");

        return variable;
    }

    public IUAVariable GetDNS1Variable(string interfaceName)
    {
        var networkInterfaceObject = editModelNetworkInterfacesObject.GetObject(interfaceName);
        var variable = networkInterfaceObject.GetVariable("DNS1");
        if (variable == null)
            throw new CoreConfigurationException("DNS1 variable not found");

        return variable;
    }

    public IUAVariable GetDNS2Variable(string interfaceName)
    {
        var networkInterfaceObject = editModelNetworkInterfacesObject.GetObject(interfaceName);
        var variable = networkInterfaceObject.GetVariable("DNS2");
        if (variable == null)
            throw new CoreConfigurationException("DNS2 variable not found");

        return variable;
    }

    public IUAVariable GetDefaultGatewayVariable(string interfaceName)
    {
        var networkInterfaceObject = editModelNetworkInterfacesObject.GetObject(interfaceName);
        var variable = networkInterfaceObject.GetVariable("DefaultGateway");
        if (variable == null)
            throw new CoreConfigurationException("DefaultGateway variable not found");

        return variable;
    }

    public IUAObject GetAdditionalIPAddressesObject(string interfaceName)
    {
        var networkInterfaceObject = editModelNetworkInterfacesObject.GetObject(interfaceName);
        var additonalIPAddressesObject = networkInterfaceObject.GetObject("AdditionalIPAddresses");
        if (additonalIPAddressesObject == null)
            throw new CoreConfigurationException("AdditionalIPAddresses object not found");

        return additonalIPAddressesObject;
    }

    readonly private IUAObject editModelNetworkInterfacesObject;
}
