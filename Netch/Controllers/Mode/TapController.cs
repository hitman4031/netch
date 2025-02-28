﻿using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Netch.Controllers.Mode
{
    public class TapController : Interface.IController
    {
        private enum NameList : int
        {
            TYPE_BYPBIND,
            TYPE_BYPLIST,
            TYPE_DNSADDR,
            TYPE_ADAPMTU,
            TYPE_TCPREST,
            TYPE_TCPTYPE,
            TYPE_TCPHOST,
            TYPE_TCPUSER,
            TYPE_TCPPASS,
            TYPE_TCPMETH,
            TYPE_TCPPROT,
            TYPE_TCPPRPA,
            TYPE_TCPOBFS,
            TYPE_TCPOBPA,
            TYPE_UDPREST,
            TYPE_UDPTYPE,
            TYPE_UDPHOST,
            TYPE_UDPUSER,
            TYPE_UDPPASS,
            TYPE_UDPMETH,
            TYPE_UDPPROT,
            TYPE_UDPPRPA,
            TYPE_UDPOBFS,
            TYPE_UDPOBPA
        }

        private static class Methods
        {
            [DllImport("tap2socks.bin", CallingConvention = CallingConvention.Cdecl)]
            public static extern bool tap_dial(NameList name, string value);

            [DllImport("tap2socks.bin", CallingConvention = CallingConvention.Cdecl)]
            public static extern bool tap_init();

            [DllImport("tap2socks.bin", CallingConvention = CallingConvention.Cdecl)]
            public static extern bool tap_free();

            [DllImport("tap2socks.bin", CallingConvention = CallingConvention.Cdecl)]
            public static extern string tap_name();

            [DllImport("tap2socks.bin", CallingConvention = CallingConvention.Cdecl)]
            public static extern ulong tap_getUP();

            [DllImport("tap2socks.bin", CallingConvention = CallingConvention.Cdecl)]
            public static extern ulong tap_getDL();
        }

        private Tools.TunTap.Outbound Outbound = new();
        private Interface.IController DNSController;

        private bool AssignInterface()
        {
            var index = Utils.RouteHelper.GetInterfaceIndexByDescription(Methods.tap_name());

            var address = Global.Config.TunMode.Network.Split('/')[0];
            var netmask = byte.Parse(Global.Config.TunMode.Network.Split('/')[1]);
            if (!Utils.RouteHelper.CreateUnicastIP(AddressFamily.InterNetwork, address, netmask, index))
            {
                return false;
            }

            NetworkInterface adapter = Utils.RouteHelper.GetInterfaceByIndex(index);
            if (adapter == null)
            {
                return false;
            }

            using (var wmi = new ManagementClass("Win32_NetworkAdapterConfiguration"))
            {
                using var ins = wmi.GetInstances();
                var ada = ins.Cast<ManagementObject>().First(m => m["Description"].ToString() == adapter.Description);

                var dns = new[] { "127.0.0.1" };
                if (Global.Config.TunMode.DNS != "aiodns")
                {
                    dns[0] = Global.Config.TunMode.DNS;
                }

                using var ord = wmi.GetMethodParameters("SetDNSServerSearchOrder");
                ord["DNSServerSearchOrder"] = dns;

                ada.InvokeMethod("SetDNSServerSearchOrder", ord, null);
            }

            return true;
        }

        private bool CreateServerRoute(Models.Server.Server s)
        {
            var addr = Utils.DNS.Fetch(s.Host);
            if (addr == IPAddress.Any)
            {
                return false;
            }

            if (addr.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return true;
            }

            return Utils.RouteHelper.CreateRoute(AddressFamily.InterNetwork, addr.ToString(), 32, this.Outbound.Gateway.ToString(), this.Outbound.Index);
        }

        private bool CreateHandleRoute(Models.Mode.TunMode.TunMode mode)
        {
            var index = Utils.RouteHelper.GetInterfaceIndexByDescription(Methods.tap_name());

            for (int i = 0; i < mode.HandleList.Count; i++)
            {
                var address = mode.HandleList[i].Split('/')[0];
                var netmask = byte.Parse(mode.HandleList[i].Split('/')[1]);

                Utils.RouteHelper.CreateRoute(AddressFamily.InterNetwork, address, netmask, Global.Config.TunMode.Gateway, index);
            }

            return true;
        }

        public bool Create(Models.Server.Server s, Models.Mode.Mode m)
        {
            Global.Logger.Info(String.Format("{0:x} tap2socks.bin", Utils.FileHelper.Checksum("bin\\tap2socks.bin")));

            if (!this.Outbound.Get())
            {
                Global.Logger.Error(String.Format("Failed to fetch outbound"));

                return false;
            }

            Methods.tap_dial(NameList.TYPE_BYPBIND, this.Outbound.Address.ToString());

            var mode = m as Models.Mode.TunMode.TunMode;
            if (mode.BypassList.Count > 0)
            {
                if (File.Exists("ipcidr.txt"))
                {
                    File.Delete("ipcidr.txt");
                }
                File.WriteAllLines("ipcidr.txt", mode.BypassList);

                Methods.tap_dial(NameList.TYPE_BYPLIST, "ipcidr.txt");
            }
            else
            {
                Methods.tap_dial(NameList.TYPE_BYPLIST, "disabled");
            }

            Methods.tap_dial(NameList.TYPE_DNSADDR, (Global.Config.TunMode.DNS == "aiodns") ? "127.0.0.1" : Global.Config.TunMode.DNS);
            Methods.tap_dial(NameList.TYPE_TCPREST, "");
            Methods.tap_dial(NameList.TYPE_UDPREST, "");

            switch (s.Type)
            {
                case Models.Server.ServerType.Socks:
                    {
                        var node = s as Models.Server.Socks.Socks;

                        Methods.tap_dial(NameList.TYPE_TCPTYPE, "Socks");
                        Methods.tap_dial(NameList.TYPE_UDPTYPE, "Socks");
                        Methods.tap_dial(NameList.TYPE_TCPHOST, $"{node.Resolve()}:{node.Port}");
                        Methods.tap_dial(NameList.TYPE_UDPHOST, $"{node.Resolve()}:{node.Port}");

                        if (!String.IsNullOrEmpty(node.Username))
                        {
                            Methods.tap_dial(NameList.TYPE_TCPUSER, node.Username);
                            Methods.tap_dial(NameList.TYPE_UDPUSER, node.Username);
                        }

                        if (!String.IsNullOrEmpty(node.Password))
                        {
                            Methods.tap_dial(NameList.TYPE_TCPPASS, node.Password);
                            Methods.tap_dial(NameList.TYPE_UDPPASS, node.Password);
                        }
                    }
                    break;
                default:
                    Methods.tap_dial(NameList.TYPE_TCPTYPE, "Socks");
                    Methods.tap_dial(NameList.TYPE_TCPHOST, $"127.0.0.1:{Global.Config.Ports.Socks}");
                    Methods.tap_dial(NameList.TYPE_UDPTYPE, "Socks");
                    Methods.tap_dial(NameList.TYPE_UDPHOST, $"127.0.0.1:{Global.Config.Ports.Socks}");
                    break;
            }

            if (!Methods.tap_init())
            {
                return false;
            }

            this.DNSController = new Other.DNS.AioDNSController();
            if (!this.DNSController.Create(s, m))
            {
                return false;
            }

            if (!this.AssignInterface())
            {
                return false;
            }

            if (!this.CreateServerRoute(s))
            {
                return false;
            }

            if (!this.CreateHandleRoute(mode))
            {
                return false;
            }

            if (File.Exists("ipcidr.txt"))
            {
                File.Delete("ipcidr.txt");
            }
            return true;
        }

        public bool Delete()
        {
            this.DNSController?.Delete();

            return Methods.tap_free();
        }
    }
}
