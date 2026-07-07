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
        /// Возвращает первый подходящий IPv4-адрес хоста вместе с именем интерфейса,
        /// MAC-адресом и описанием.
        /// </summary>
        /// <returns>Кортеж: адрес, имя интерфейса, MAC-адрес, описание.</returns>
        public static (string address, string name, string mac, string descr) GetFirstIPAddress()
        {
            // Получаем список всех сетевых интерфейсов (обычно по одному на сетевую карту,
            // модемное и VPN-подключение).
            NetworkInterface[] networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (NetworkInterface network in networkInterfaces.Where(x => x.OperationalStatus == OperationalStatus.Up))
            {
                // Читаем IP-конфигурацию каждого интерфейса.
                IPInterfaceProperties properties = network.GetIPProperties();

                if (properties.GatewayAddresses.Count == 0)
                {
                    continue;
                }

                // У интерфейса может быть несколько IP-адресов — берём IPv4.
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
