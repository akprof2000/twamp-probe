// Ignore Spelling: emr spi twamp

using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SPI.Twamp.Probe.Environment
{
    /// <summary>
    /// Вспомогательные функции уровня хоста (сведения о сетевых интерфейсах и т. п.).
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
    }
}
