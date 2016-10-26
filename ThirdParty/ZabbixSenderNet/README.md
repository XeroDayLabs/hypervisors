# ZabbixSenderNet
ZabbixSenderNet implements [Zabbix Sender Protocol 2.0](https://www.zabbix.org/wiki/Docs/protocols/zabbix_sender/2.0) by C#. .NET Framework 4.0 or later is required.

# Installation

NuGet package is available [here](https://www.nuget.org/packages/ZabbixSender/).

```PowerShell
PM> Install-Package ZabbixSender
```

# Interface
```C#
/// zabbixServer: FQDN or IP address of Zabbix server instance 
/// port        : TCP port number of Zabbix server
public Sender(string zabbixServer, int port = 10051)

/// host    : monitored host name
/// itemKey : Zabbix Trapper item key
/// value   : value to send
/// timeout : (Optional) TCP recv timeout. If Zabbix server doesn't respond the request, 
///           the method throws System.TimeoutException.
public SenderResponse Send(string host, string itemKey, string value, int timeout = 500)
```
# Example

```C#
var zabbixServer = "192.168.0.1";
var sender = new Ysq.Zabbix.Sender(zabbixServer);
var response = sender.Send("Host1", "trapper.item1", "10");
Console.WriteLine(reponse.Response); // "success" or "fail"
Console.WriteLine(response.Info); // e.g. "Processed 1 Failed 0 Total 1 Seconds spent 0.000253"
```
