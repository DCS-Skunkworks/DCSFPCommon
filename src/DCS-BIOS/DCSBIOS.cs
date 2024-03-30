
// // ReSharper disable All
/*
 * Do not adhere to naming standard in DCS-BIOS code, standard are based on DCS-BIOS json files and byte streamnaming
 */

using System.Diagnostics;
using DCS_BIOS.EventArgs;
using DCS_BIOS.misc;
using DCS_BIOS.StringClasses;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Timers;

namespace DCS_BIOS
{
    [Flags]
    public enum DcsBiosNotificationMode
    {
        Parse = 2,
        PassThrough = 4
    }

    /// <summary>
    /// Main class in project. Sends commands to DCS-BIOS and receives data about all cockpit controls
    /// in the aircraft.
    /// </summary>
    public class DCSBIOS : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static DCSBIOS _dcsBIOSInstance;

        /************************
        **********UDP************
        ************************/
        private UdpClient _udpReceiveClient;
        private UdpClient _udpSendClient;
        private Thread _dcsbiosListeningThread;
        private readonly System.Timers.Timer _udpReceiveThrottleTimer = new(10) { AutoReset = true }; //Throttle UDP receive every 10 ms in case nothing is available
        private AutoResetEvent _udpReceiveThrottleAutoResetEvent = new(false);
        public string ReceiveFromIpUdp { get; set; } = "239.255.50.10";
        public string SendToIpUdp { get; set; } = "127.0.0.1";
        public int ReceivePortUdp { get; set; } = 5010;
        public int SendPortUdp { get; set; } = 7778;
        private IPEndPoint _ipEndPointReceiverUdp;
        private IPEndPoint _ipEndPointSenderUdp;
        public string ReceivedDataUdp { get; } = null;
        /************************
        *************************
        ************************/

        private readonly Channel<Tuple<string,string>> _dcsbiosCommandsChannel = Channel.CreateUnbounded<Tuple<string, string>>();
        private AutoResetEvent _dcsbiosCommandWaitingResetEvent = new(false);

        private readonly object _lockExceptionObject = new();
        private Exception _lastException;
        private DCSBIOSProtocolParser _dcsProtocolParser;
        private readonly DcsBiosNotificationMode _dcsBiosNotificationMode;
        private volatile bool _isRunning;

        private readonly object _queueLockObject = new();

        public bool IsRunning
        {
            get => _isRunning;
        }

        public DCSBIOS(string ipFromUdp, string ipToUdp, int portFromUdp, int portToUdp, DcsBiosNotificationMode dcsNotificationMode)
        {

            if (!string.IsNullOrEmpty(ipFromUdp) && IPAddress.TryParse(ipFromUdp, out _))
            {
                ReceiveFromIpUdp = ipFromUdp;
            }

            if (!string.IsNullOrEmpty(ipToUdp) && IPAddress.TryParse(ipToUdp, out _))
            {
                SendToIpUdp = ipToUdp;
            }

            if (portFromUdp > 0)
            {
                ReceivePortUdp = portFromUdp;
            }

            if (portToUdp > 0)
            {
                SendPortUdp = portToUdp;
            }

            _dcsBiosNotificationMode = dcsNotificationMode;
            _dcsBIOSInstance = this;

            Startup();
        }

        public void Dispose()
        {
            DCSBIOSStringManager.Close();
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                Shutdown();
            }
        }

        public void Startup()
        {
            try
            {
                if (_isRunning)
                {
                    return;
                }

                _udpReceiveThrottleAutoResetEvent = new(false);

                _dcsbiosCommandWaitingResetEvent = new(false);

                _ = Task.Run(SendCommandsAsync);

                _dcsProtocolParser = DCSBIOSProtocolParser.GetParser();

                _ipEndPointReceiverUdp = new IPEndPoint(IPAddress.Any, ReceivePortUdp);
                _ipEndPointSenderUdp = new IPEndPoint(IPAddress.Parse(SendToIpUdp), SendPortUdp);

                _udpReceiveClient = new UdpClient();
                _udpReceiveClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpReceiveClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 200);
                _udpReceiveClient.Client.Bind(_ipEndPointReceiverUdp);
                _udpReceiveClient.JoinMulticastGroup(IPAddress.Parse(ReceiveFromIpUdp));

                _udpSendClient = new UdpClient();
                _udpSendClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpSendClient.EnableBroadcast = true;

                _udpReceiveThrottleTimer.Elapsed += UdpReceiveThrottleTimer_Elapsed;
                _udpReceiveThrottleTimer.Start();
                _dcsbiosListeningThread = new Thread(ReceiveDataUdp);

                _isRunning = true;
                _dcsProtocolParser.Startup();
                _dcsbiosListeningThread.Start();
            }
            catch (Exception ex)
            {
                SetLastException(ex);
                Logger.Error(ex, "DCSBIOS.Startup()");
                if (_udpReceiveClient != null && _udpReceiveClient.Client.Connected)
                {
                    _udpReceiveClient.Close();
                    _udpReceiveClient = null;
                }
                if (_udpSendClient != null && _udpSendClient.Client != null && _udpSendClient.Client.Connected)
                {
                    _udpSendClient.Close();
                    _udpSendClient = null;
                }
            }
        }

        public void Shutdown()
        {
            try
            {
                _isRunning = false;

                _dcsbiosCommandWaitingResetEvent?.Set();
                _dcsbiosCommandWaitingResetEvent?.Close();
                _dcsbiosCommandWaitingResetEvent?.Dispose();
                _dcsbiosCommandWaitingResetEvent = null;

                _udpReceiveThrottleTimer.Stop();

                _udpReceiveThrottleAutoResetEvent?.Set();
                _udpReceiveThrottleAutoResetEvent?.Close();
                _udpReceiveThrottleAutoResetEvent?.Dispose();
                _udpReceiveThrottleAutoResetEvent = null;

                _udpReceiveClient?.Close();
                _udpReceiveClient?.Dispose();
                _udpReceiveClient = null;

                _udpSendClient?.Close();
                _udpSendClient?.Dispose();
                _udpSendClient = null;

                _dcsProtocolParser?.Shutdown();
                _dcsProtocolParser?.Dispose();
                _dcsProtocolParser = null;
            }
            catch (Exception ex)
            {
                SetLastException(ex);
                Logger.Error(ex, "DCSBIOS.Shutdown()");
            }
        }

        private void UdpReceiveThrottleTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            _udpReceiveThrottleAutoResetEvent?.Set();
        }

        public void ReceiveDataUdp()
        {
            try
            {
                while (_isRunning)
                {
                    try
                    {
                        if (_udpReceiveClient.Available > 0)
                        {
                            BIOSEventHandler.ConnectionActive(this);
                            var byteData = _udpReceiveClient.Receive(ref _ipEndPointReceiverUdp);
                            if ((_dcsBiosNotificationMode & DcsBiosNotificationMode.Parse) == DcsBiosNotificationMode.Parse)
                            {
                                _dcsProtocolParser.AddArray(byteData);
                            }
                            if ((_dcsBiosNotificationMode & DcsBiosNotificationMode.PassThrough) == DcsBiosNotificationMode.PassThrough)
                            {
                                BIOSEventHandler.AsyncDCSBIOSBulkDataAvailable(this, byteData);
                            }
                            continue;
                        }
                        _udpReceiveThrottleAutoResetEvent.WaitOne(); // Minimizes CPU hit
                    }
                    catch (SocketException)
                    {
                        continue;
                    }
                }

            }
            catch (Exception ex)
            {
                if (!ex.Message.Contains("WSACancelBlockingCall"))
                {
                    SetLastException(ex);
                    Logger.Error(ex, "DCSBIOS.ReceiveData()");
                }
            }
        }

        public static DCSBIOS GetInstance()
        {
            return _dcsBIOSInstance;
        }
        private void SetLastException(Exception ex)
        {
            try
            {
                if (ex == null)
                {
                    return;
                }
                Logger.Error(ex, "Via DCSBIOS.SetLastException()");
                var message = ex.GetType() + Environment.NewLine + ex.Message + Environment.NewLine + ex.StackTrace;
                lock (_lockExceptionObject)
                {
                    _lastException = new Exception(message);
                }
            }
            catch (Exception)
            {
                // ignore
            }
        }

        public Exception GetLastException(bool resetException = false)
        {
            Exception result;
            lock (_lockExceptionObject)
            {
                result = _lastException;
                if (resetException)
                {
                    _lastException = null;
                }
            }
            return result;
        }

        public bool HasLastException()
        {
            lock (_lockExceptionObject)
            {
                return _lastException != null;
            }
        }

        public static async Task SendAsync(string stringData)
        {
            await _dcsBIOSInstance.QueueDCSBIOSCommandAsync(null, stringData);
        }

        public static async Task SendAsync(string sender, string stringData)
        {
            await _dcsBIOSInstance.QueueDCSBIOSCommandAsync(sender, stringData);
        }

        public static async Task SendAsync(string sender, string[] stringArray)
        {
            if (stringArray != null)
            {
                await SendAsync(sender, stringArray.ToList());
            }
        }

        public static async Task SendAsync(string sender, List<string> stringList)
        {
            if (stringList != null)
            {
                foreach (var command in stringList)
                {
                    await _dcsBIOSInstance.QueueDCSBIOSCommandAsync(sender, command);
                }
            }
        }
        
        private async Task QueueDCSBIOSCommandAsync(string sender, string dcsbiosCommand)
        {
            if (dcsbiosCommand == null || dcsbiosCommand.Trim().Length == 0) return;

            var tuple = new Tuple<string, string>(sender, dcsbiosCommand);
            var cts = new CancellationTokenSource(DCSBIOSConstants.MS100);
            await _dcsbiosCommandsChannel.Writer.WriteAsync(tuple, cts.Token);

            lock (_queueLockObject)
            {
                _dcsbiosCommandWaitingResetEvent.Set();
            }
        }

        private async Task SendCommandsAsync()
        {
            while (true)
            {
                try
                {
                    _dcsbiosCommandWaitingResetEvent.WaitOne();

                    if (!_isRunning) break;

                    var cts = new CancellationTokenSource(DCSBIOSConstants.MS100);
                    var tuple = await _dcsbiosCommandsChannel.Reader.ReadAsync(cts.Token);

                    var sender = tuple.Item1;
                    var dcsbiosCommand = tuple.Item2;
                    if (dcsbiosCommand == null || dcsbiosCommand.Trim().Length == 0) return;

                    Debug.WriteLine($"Sending command : {dcsbiosCommand}");

                    var unicodeBytes = Encoding.Unicode.GetBytes(dcsbiosCommand);
                    var asciiBytes = new List<byte>(dcsbiosCommand.Length);
                    asciiBytes.AddRange(Encoding.Convert(Encoding.Unicode, Encoding.ASCII, unicodeBytes));
                    await _udpSendClient.SendAsync(asciiBytes.ToArray(), asciiBytes.ToArray().Length, _ipEndPointSenderUdp);

                    BIOSEventHandler.DCSBIOSCommandWasSent(sender, dcsbiosCommand);
                }
                catch (OperationCanceledException e)
                {
                    Logger.Error("DCS-BIOS.AsyncSendCommands failed => {0}", e);
                }
                catch (IOException e)
                {
                    Logger.Error("DCS-BIOS.AsyncSendCommands failed => {0}", e);
                }
                catch (Exception e)
                {
                    Logger.Error("DCS-BIOS.AsyncSendCommands failed => {0}", e);
                }
            }
        }
    }
}
