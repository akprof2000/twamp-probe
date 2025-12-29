// Ignore Spelling: emr spi twamp

using NLog;
using SPI.Twamp.Probe.Contracts;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SPI.Twamp.Probe.Environment
{
    /// <summary>
    /// 
    /// </summary>
    public static class HostFunctions
    {
        /// <summary>
        /// Gets the first ip address.
        /// </summary>
        /// <returns></returns>
        public static (string address, string name, string mac, string descr) GetFirstIPAddress()
        {
            // Get a list of all network interfaces (usually one per network card, dial-up, and VPN connection) 
            NetworkInterface[] networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (NetworkInterface network in networkInterfaces.Where(x => x.OperationalStatus == OperationalStatus.Up))
            {


                // Read the IP configuration for each network 
                IPInterfaceProperties properties = network.GetIPProperties();

                if (properties.GatewayAddresses.Count == 0)
                {
                    continue;
                }

                // Each network interface may have multiple IP addresses 
                IEnumerable<UnicastIPAddressInformation> address = properties.UnicastAddresses.Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork);


                if (address.Any())
                {
                    return (address.First().Address.ToString(), network.Name, network.GetPhysicalAddress().ToString(), network.Description);
                }
            }

            return ("empty", "none", "none", "none");
        }


        /// <summary>
        /// Does the work.
        /// </summary>
        /// <param name="task">The task.</param>
        /// <param name="node">The node.</param>
        /// <param name="configuration">The configuration.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="endJob">The end job.</param>
        /// <param name="bag">The bag.</param>
        /// <param name="stoppingToken">The stopping token.</param>
        /// <returns></returns>
        public static void DoWork(TaskInfo task, string node, IConfiguration configuration, Logger logger, Action endJob, ConcurrentBag<ActionData> bag, CancellationToken stoppingToken)
        {
            logger.Info("Start task by parameters {@TaskInfo}", task);
            string? execute = "";
            StringBuilder arg = new();
            switch (task.Mode)
            {
                case TaskMode.WinPing:
                    execute = configuration["ping:name"];
                    _ = arg.Append(node);
                    if (task.Parameters.Any())
                    {
                        foreach (string item in task.Parameters.Values)
                        {
                            _ = arg.Append(' ');
                            _ = arg.Append(item);
                        }
                    }
                    else
                    {
                        _ = arg.Append(' ');
                        _ = arg.Append(configuration["ping:default"]);
                    }
                    break;
                case TaskMode.TWamp:
                    execute = configuration["twamp:name"];
                    if (task.Parameters.Any())
                    {
                        foreach (string item in task.Parameters.Values)
                        {
                            _ = arg.Append(' ');
                            _ = arg.Append(item);
                        }
                    }
                    else
                    {
                        _ = arg.Append(' ');
                        _ = arg.Append(configuration["twamp:default"]);
                    }
                    _ = arg.Append(node);
                    break;
                default:
                    break;
            }

            for (int j = 0; j < task.Circles; j++)
            {

                for (int i = 0; i < task.Repeats; i++)
                {
                    using Process? p = Process.Start(new ProcessStartInfo
                    {
                        FileName = execute, // File to execute
                        Arguments = arg.ToString(), // arguments to use
                        UseShellExecute = false, // use process creation semantics
                        RedirectStandardOutput = true, // redirect standard output to this Process object
                        CreateNoWindow = true, // if this is a terminal app, don't show it
                        RedirectStandardInput = true,
                        RedirectStandardError = true,
                        WindowStyle = ProcessWindowStyle.Hidden // if this is a terminal app, don't show it
                    });
                    if (p != null)
                    {
                        // Wait for the process to finish executing
                        _ = p.WaitForExitAsync(stoppingToken);
                        // display what the process output
                        string con = p.StandardOutput.ReadToEnd();
                        logger.Info("Console read {Console}", con);
                        string err = p.StandardError.ReadToEnd();
                        if (!string.IsNullOrEmpty(err))
                        {
                            logger.Info("Console read {Console}", err);
                        }
                        bag.Add(new ActionData { Console = con, ErrorConsole = err, EndNode = node, IPAddress = task.IpAddress, TaskId = task.Id, RequestInfo = task.RequestInfo });
                        endJob();

                    }
                    if (task.Circles - 1 > j)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(task.PauseSec));
                    }
                }
            }
        }
    }
}
