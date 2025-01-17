using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace Rssdp.Infrastructure
{
    /// <summary>
    /// Allows you to search the network for a particular device, device types, or UPnP service types. Also listenings for broadcast notifications of device availability and raises events to indicate changes in status.
    /// </summary>
    public class SsdpDeviceLocator : DisposableManagedObjectBase
    {
        private List<DiscoveredSsdpDevice> _Devices;
        private ISsdpCommunicationsServer _CommunicationsServer;

        private Timer _BroadcastTimer;
        private object _timerLock = new();

        private string _OSName;

        private string _OSVersion;

        private readonly TimeSpan DefaultSearchWaitTime = TimeSpan.FromSeconds(4);
        private readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Default constructor.
        /// </summary>
        public SsdpDeviceLocator(
            ISsdpCommunicationsServer communicationsServer,
            string osName,
            string osVersion)
        {
            ArgumentNullException.ThrowIfNull(communicationsServer);
            ArgumentNullException.ThrowIfNullOrEmpty(osName);
            ArgumentNullException.ThrowIfNullOrEmpty(osVersion);

            _OSName = osName;
            _OSVersion = osVersion;
            _CommunicationsServer = communicationsServer;
            _CommunicationsServer.ResponseReceived += CommsServer_ResponseReceived;

            _Devices = new List<DiscoveredSsdpDevice>();
        }

        /// <summary>
        /// Raised for when
        /// <list type="bullet">
        /// <item>An 'alive' notification is received that a device, regardless of whether or not that device is not already in the cache or has previously raised this event.</item>
        /// <item>For each item found during a device <see cref="SearchAsync(System.Threading.CancellationToken)"/> (cached or not), allowing clients to respond to found devices before the entire search is complete.</item>
        /// <item>Only if the notification type matches the <see cref="NotificationFilter"/> property. By default the filter is null, meaning all notifications raise events (regardless of ant </item>
        /// </list>
        /// <para>This event may be raised from a background thread, if interacting with UI or other objects with specific thread affinity invoking to the relevant thread is required.</para>
        /// </summary>
        /// <seealso cref="NotificationFilter"/>
        /// <seealso cref="DeviceUnavailable"/>
        /// <seealso cref="StartListeningForNotifications"/>
        /// <seealso cref="StopListeningForNotifications"/>
        public event EventHandler<DeviceAvailableEventArgs> DeviceAvailable;

        /// <summary>
        /// Raised when a notification is received that indicates a device has shutdown or otherwise become unavailable.
        /// </summary>
        /// <remarks>
        /// <para>Devices *should* broadcast these types of notifications, but not all devices do and sometimes (in the event of power loss for example) it might not be possible for a device to do so. You should also implement error handling when trying to contact a device, even if RSSDP is reporting that device as available.</para>
        /// <para>This event is only raised if the notification type matches the <see cref="NotificationFilter"/> property. A null or empty string for the <see cref="NotificationFilter"/> will be treated as no filter and raise the event for all notifications.</para>
        /// <para>The <see cref="DeviceUnavailableEventArgs.DiscoveredDevice"/> property may contain either a fully complete <see cref="DiscoveredSsdpDevice"/> instance, or one containing just a USN and NotificationType property. Full information is available if the device was previously discovered and cached, but only partial information if a byebye notification was received for a previously unseen or expired device.</para>
        /// <para>This event may be raised from a background thread, if interacting with UI or other objects with specific thread affinity invoking to the relevant thread is required.</para>
        /// </remarks>
        /// <seealso cref="NotificationFilter"/>
        /// <seealso cref="DeviceAvailable"/>
        /// <seealso cref="StartListeningForNotifications"/>
        /// <seealso cref="StopListeningForNotifications"/>
        public event EventHandler<DeviceUnavailableEventArgs> DeviceUnavailable;

        public void RestartBroadcastTimer(TimeSpan dueTime, TimeSpan period)
        {
            lock (_timerLock)
            {
                if (_BroadcastTimer is null)
                {
                    _BroadcastTimer = new Timer(OnBroadcastTimerCallback, null, dueTime, period);
                }
                else
                {
                    _BroadcastTimer.Change(dueTime, period);
                }
            }
        }

        public void DisposeBroadcastTimer()
        {
            lock (_timerLock)
            {
                if (_BroadcastTimer is not null)
                {
                    _BroadcastTimer.Dispose();
                    _BroadcastTimer = null;
                }
            }
        }

        private async void OnBroadcastTimerCallback(object state)
        {
            if (IsDisposed)
            {
                return;
            }

            StartListeningForNotifications();
            RemoveExpiredDevicesFromCache();

            try
            {
                await SearchAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Performs a search for all devices using the default search timeout.
        /// </summary>
        private Task SearchAsync(CancellationToken cancellationToken)
        {
            return SearchAsync(SsdpConstants.SsdpDiscoverAllSTHeader, DefaultSearchWaitTime, cancellationToken);
        }

        /// <summary>
        /// Performs a search for the specified search target (criteria) and default search timeout.
        /// </summary>
        /// <param name="searchTarget">The criteria for the search. Value can be;
        /// <list type="table">
        /// <item><term>Root devices</term><description>upnp:rootdevice</description></item>
        /// <item><term>Specific device by UUID</term><description>uuid:&lt;device uuid&gt;</description></item>
        /// <item><term>Device type</term><description>Fully qualified device type starting with urn: i.e urn:schemas-upnp-org:Basic:1</description></item>
        /// </list>
        /// </param>
        private Task SearchAsync(string searchTarget)
        {
            return SearchAsync(searchTarget, DefaultSearchWaitTime, CancellationToken.None);
        }

        /// <summary>
        /// Performs a search for all devices using the specified search timeout.
        /// </summary>
        /// <param name="searchWaitTime">The amount of time to wait for network responses to the search request. Longer values will likely return more devices, but increase search time. A value between 1 and 5 seconds is recommended by the UPnP 1.1 specification, this method requires the value be greater 1 second if it is not zero. Specify TimeSpan.Zero to return only devices already in the cache.</param>
        private Task SearchAsync(TimeSpan searchWaitTime)
        {
            return SearchAsync(SsdpConstants.SsdpDiscoverAllSTHeader, searchWaitTime, CancellationToken.None);
        }

        private Task SearchAsync(string searchTarget, TimeSpan searchWaitTime, CancellationToken cancellationToken)
        {
            if (searchTarget is null)
            {
                throw new ArgumentNullException(nameof(searchTarget));
            }

            if (searchTarget.Length == 0)
            {
                throw new ArgumentException("searchTarget cannot be an empty string.", nameof(searchTarget));
            }

            if (searchWaitTime.TotalSeconds < 0)
            {
                throw new ArgumentException("searchWaitTime must be a positive time.");
            }

            if (searchWaitTime.TotalSeconds > 0 && searchWaitTime.TotalSeconds <= 1)
            {
                throw new ArgumentException("searchWaitTime must be zero (if you are not using the result and relying entirely in the events), or greater than one second.");
            }

            ThrowIfDisposed();

            return BroadcastDiscoverMessage(searchTarget, SearchTimeToMXValue(searchWaitTime), cancellationToken);
        }

        /// <summary>
        /// Starts listening for broadcast notifications of service availability.
        /// </summary>
        /// <remarks>
        /// <para>When called the system will listen for 'alive' and 'byebye' notifications. This can speed up searching, as well as provide dynamic notification of new devices appearing on the network, and previously discovered devices disappearing.</para>
        /// </remarks>
        /// <seealso cref="StopListeningForNotifications"/>
        /// <seealso cref="DeviceAvailable"/>
        /// <seealso cref="DeviceUnavailable"/>
        /// <exception cref="ObjectDisposedException">Throw if the <see cref="DisposableManagedObjectBase.IsDisposed"/>  ty is true.</exception>
        public void StartListeningForNotifications()
        {
            _CommunicationsServer.RequestReceived -= CommsServer_RequestReceived;
            _CommunicationsServer.RequestReceived += CommsServer_RequestReceived;
            _CommunicationsServer.BeginListeningForMulticast();
        }

        /// <summary>
        /// Stops listening for broadcast notifications of service availability.
        /// </summary>
        /// <remarks>
        /// <para>Does nothing if this instance is not already listening for notifications.</para>
        /// </remarks>
        /// <seealso cref="StartListeningForNotifications"/>
        /// <seealso cref="DeviceAvailable"/>
        /// <seealso cref="DeviceUnavailable"/>
        /// <exception cref="ObjectDisposedException">Throw if the <see cref="DisposableManagedObjectBase.IsDisposed"/> property is true.</exception>
        public void StopListeningForNotifications()
        {
            ThrowIfDisposed();

            _CommunicationsServer.RequestReceived -= CommsServer_RequestReceived;
        }

        /// <summary>
        /// Raises the <see cref="DeviceAvailable"/> event.
        /// </summary>
        /// <seealso cref="DeviceAvailable"/>
        protected virtual void OnDeviceAvailable(DiscoveredSsdpDevice device, bool isNewDevice, IPAddress IPAddress)
        {
            if (IsDisposed)
            {
                return;
            }

            var handlers = DeviceAvailable;
            if (handlers is not null)
            {
                handlers(this, new DeviceAvailableEventArgs(device, isNewDevice)
                {
                    RemoteIPAddress = IPAddress
                });
            }
        }

        /// <summary>
        /// Raises the <see cref="DeviceUnavailable"/> event.
        /// </summary>
        /// <param name="device">A <see cref="DiscoveredSsdpDevice"/> representing the device that is no longer available.</param>
        /// <param name="expired">True if the device expired from the cache without being renewed, otherwise false to indicate the device explicitly notified us it was being shutdown.</param>
        /// <seealso cref="DeviceUnavailable"/>
        protected virtual void OnDeviceUnavailable(DiscoveredSsdpDevice device, bool expired)
        {
            if (IsDisposed)
            {
                return;
            }

            var handlers = DeviceUnavailable;
            if (handlers is not null)
            {
                handlers(this, new DeviceUnavailableEventArgs(device, expired));
            }
        }

        /// <summary>
        /// Sets or returns a string containing the filter for notifications. Notifications not matching the filter will not raise the <see cref="ISsdpDeviceLocator.DeviceAvailable"/> or <see cref="ISsdpDeviceLocator.DeviceUnavailable"/> events.
        /// </summary>
        /// <remarks>
        /// <para>Device alive/byebye notifications whose NT header does not match this filter value will still be captured and cached internally, but will not raise events about device availability. Usually used with either a device type of uuid NT header value.</para>
        /// <para>If the value is null or empty string then, all notifications are reported.</para>
        /// <para>Example filters follow;</para>
        /// <example>upnp:rootdevice</example>
        /// <example>urn:schemas-upnp-org:device:WANDevice:1</example>
        /// <example>uuid:9F15356CC-95FA-572E-0E99-85B456BD3012</example>
        /// </remarks>
        /// <seealso cref="ISsdpDeviceLocator.DeviceAvailable"/>
        /// <seealso cref="ISsdpDeviceLocator.DeviceUnavailable"/>
        /// <seealso cref="ISsdpDeviceLocator.StartListeningForNotifications"/>
        /// <seealso cref="ISsdpDeviceLocator.StopListeningForNotifications"/>
        public string NotificationFilter
        {
            get;
            set;
        }

        /// <summary>
        /// Disposes this object and all internal resources. Stops listening for all network messages.
        /// </summary>
        /// <param name="disposing">True if managed resources should be disposed, or false is only unmanaged resources should be cleaned up.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeBroadcastTimer();

                var commsServer = _CommunicationsServer;
                _CommunicationsServer = null;
                if (commsServer is not null)
                {
                    commsServer.ResponseReceived -= CommsServer_ResponseReceived;
                    commsServer.RequestReceived -= CommsServer_RequestReceived;
                }
            }
        }

        private void AddOrUpdateDiscoveredDevice(DiscoveredSsdpDevice device, IPAddress IPAddress)
        {
            bool isNewDevice = false;
            lock (_Devices)
            {
                var existingDevice = FindExistingDeviceNotification(_Devices, device.NotificationType, device.Usn);
                if (existingDevice is null)
                {
                    _Devices.Add(device);
                    isNewDevice = true;
                }
                else
                {
                    _Devices.Remove(existingDevice);
                    _Devices.Add(device);
                }
            }

            DeviceFound(device, isNewDevice, IPAddress);
        }

        private void DeviceFound(DiscoveredSsdpDevice device, bool isNewDevice, IPAddress IPAddress)
        {
            if (!NotificationTypeMatchesFilter(device))
            {
                return;
            }

            OnDeviceAvailable(device, isNewDevice, IPAddress);
        }

        private bool NotificationTypeMatchesFilter(DiscoveredSsdpDevice device)
        {
            return String.IsNullOrEmpty(this.NotificationFilter)
                || this.NotificationFilter == SsdpConstants.SsdpDiscoverAllSTHeader
                || device.NotificationType == this.NotificationFilter;
        }

        private Task BroadcastDiscoverMessage(string serviceType, TimeSpan mxValue, CancellationToken cancellationToken)
        {
            const string header = "M-SEARCH * HTTP/1.1";

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            values["HOST"] = string.Format(CultureInfo.InvariantCulture, "{0}:{1}", SsdpConstants.MulticastLocalAdminAddress, SsdpConstants.MulticastPort);
            values["USER-AGENT"] = string.Format(CultureInfo.InvariantCulture, "{0}/{1} UPnP/1.0 RSSDP/{2}", _OSName, _OSVersion, SsdpConstants.ServerVersion);
            values["MAN"] = "\"ssdp:discover\"";

            // Search target
            values["ST"] = "ssdp:all";

            // Seconds to delay response
            values["MX"] = "3";

            var message = BuildMessage(header, values);

            return _CommunicationsServer.SendMulticastMessage(message, null, cancellationToken);
        }

        private void ProcessSearchResponseMessage(HttpResponseMessage message, IPAddress IPAddress)
        {
            if (!message.IsSuccessStatusCode)
            {
                return;
            }

            var location = GetFirstHeaderUriValue("Location", message);
            if (location is not null)
            {
                var device = new DiscoveredSsdpDevice()
                {
                    DescriptionLocation = location,
                    Usn = GetFirstHeaderStringValue("USN", message),
                    NotificationType = GetFirstHeaderStringValue("ST", message),
                    CacheLifetime = CacheAgeFromHeader(message.Headers.CacheControl),
                    AsAt = DateTimeOffset.Now,
                    ResponseHeaders = message.Headers
                };

                AddOrUpdateDiscoveredDevice(device, IPAddress);
            }
        }

        private void ProcessNotificationMessage(HttpRequestMessage message, IPAddress IPAddress)
        {
            if (string.Compare(message.Method.Method, "Notify", StringComparison.OrdinalIgnoreCase) != 0)
            {
                return;
            }

            var notificationType = GetFirstHeaderStringValue("NTS", message);
            if (string.Compare(notificationType, SsdpConstants.SsdpKeepAliveNotification, StringComparison.OrdinalIgnoreCase) == 0)
            {
                ProcessAliveNotification(message, IPAddress);
            }
            else if (string.Compare(notificationType, SsdpConstants.SsdpByeByeNotification, StringComparison.OrdinalIgnoreCase) == 0)
            {
                ProcessByeByeNotification(message);
            }
        }

        private void ProcessAliveNotification(HttpRequestMessage message, IPAddress IPAddress)
        {
            var location = GetFirstHeaderUriValue("Location", message);
            if (location is not null)
            {
                var device = new DiscoveredSsdpDevice()
                {
                    DescriptionLocation = location,
                    Usn = GetFirstHeaderStringValue("USN", message),
                    NotificationType = GetFirstHeaderStringValue("NT", message),
                    CacheLifetime = CacheAgeFromHeader(message.Headers.CacheControl),
                    AsAt = DateTimeOffset.Now,
                    ResponseHeaders = message.Headers
                };

                AddOrUpdateDiscoveredDevice(device, IPAddress);
            }
        }

        private void ProcessByeByeNotification(HttpRequestMessage message)
        {
            var notficationType = GetFirstHeaderStringValue("NT", message);
            if (!string.IsNullOrEmpty(notficationType))
            {
                var usn = GetFirstHeaderStringValue("USN", message);

                if (!DeviceDied(usn, false))
                {
                    var deadDevice = new DiscoveredSsdpDevice()
                    {
                        AsAt = DateTime.UtcNow,
                        CacheLifetime = TimeSpan.Zero,
                        DescriptionLocation = null,
                        NotificationType = GetFirstHeaderStringValue("NT", message),
                        Usn = usn,
                        ResponseHeaders = message.Headers
                    };

                    if (NotificationTypeMatchesFilter(deadDevice))
                    {
                        OnDeviceUnavailable(deadDevice, false);
                    }
                }
            }
        }

        private string GetFirstHeaderStringValue(string headerName, HttpResponseMessage message)
        {
            string retVal = null;
            if (message.Headers.Contains(headerName))
            {
                message.Headers.TryGetValues(headerName, out var values);
                if (values is not null)
                {
                    retVal = values.FirstOrDefault();
                }
            }

            return retVal;
        }

        private string GetFirstHeaderStringValue(string headerName, HttpRequestMessage message)
        {
            string retVal = null;
            if (message.Headers.Contains(headerName))
            {
                message.Headers.TryGetValues(headerName, out var values);
                if (values is not null)
                {
                    retVal = values.FirstOrDefault();
                }
            }

            return retVal;
        }

        private Uri GetFirstHeaderUriValue(string headerName, HttpRequestMessage request)
        {
            string value = null;
            if (request.Headers.Contains(headerName))
            {
                request.Headers.TryGetValues(headerName, out var values);
                if (values is not null)
                {
                    value = values.FirstOrDefault();
                }
            }

            Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var retVal);
            return retVal;
        }

        private Uri GetFirstHeaderUriValue(string headerName, HttpResponseMessage response)
        {
            string value = null;
            if (response.Headers.Contains(headerName))
            {
                response.Headers.TryGetValues(headerName, out var values);
                if (values is not null)
                {
                    value = values.FirstOrDefault();
                }
            }

            Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var retVal);
            return retVal;
        }

        private TimeSpan CacheAgeFromHeader(System.Net.Http.Headers.CacheControlHeaderValue headerValue)
        {
            if (headerValue is null)
            {
                return TimeSpan.Zero;
            }

            return headerValue.MaxAge ?? headerValue.SharedMaxAge ?? TimeSpan.Zero;
        }

        private void RemoveExpiredDevicesFromCache()
        {
            DiscoveredSsdpDevice[] expiredDevices = null;
            lock (_Devices)
            {
                expiredDevices = (from device in _Devices where device.IsExpired() select device).ToArray();

                foreach (var device in expiredDevices)
                {
                    if (IsDisposed)
                    {
                        return;
                    }

                    _Devices.Remove(device);
                }
            }

            // Don't do this inside lock because DeviceDied raises an event
            // which means public code may execute during lock and cause
            // problems.
            foreach (var expiredUsn in (from expiredDevice in expiredDevices select expiredDevice.Usn).Distinct())
            {
                if (IsDisposed)
                {
                    return;
                }

                DeviceDied(expiredUsn, true);
            }
        }

        private bool DeviceDied(string deviceUsn, bool expired)
        {
            List<DiscoveredSsdpDevice> existingDevices = null;
            lock (_Devices)
            {
                existingDevices = FindExistingDeviceNotifications(_Devices, deviceUsn);
                foreach (var existingDevice in existingDevices)
                {
                    if (IsDisposed)
                    {
                        return true;
                    }

                    _Devices.Remove(existingDevice);
                }
            }

            if (existingDevices is not null && existingDevices.Count > 0)
            {
                foreach (var removedDevice in existingDevices)
                {
                    if (NotificationTypeMatchesFilter(removedDevice))
                    {
                        OnDeviceUnavailable(removedDevice, expired);
                    }
                }

                return true;
            }

            return false;
        }

        private TimeSpan SearchTimeToMXValue(TimeSpan searchWaitTime)
        {
            if (searchWaitTime.TotalSeconds < 2 || searchWaitTime == TimeSpan.Zero)
            {
                return OneSecond;
            }

            return searchWaitTime.Subtract(OneSecond);
        }

        private DiscoveredSsdpDevice FindExistingDeviceNotification(IEnumerable<DiscoveredSsdpDevice> devices, string notificationType, string usn)
        {
            foreach (var d in devices)
            {
                if (d.NotificationType == notificationType && d.Usn == usn)
                {
                    return d;
                }
            }

            return null;
        }

        private List<DiscoveredSsdpDevice> FindExistingDeviceNotifications(IList<DiscoveredSsdpDevice> devices, string usn)
        {
            var list = new List<DiscoveredSsdpDevice>();

            foreach (var d in devices)
            {
                if (d.Usn == usn)
                {
                    list.Add(d);
                }
            }

            return list;
        }

        private void CommsServer_ResponseReceived(object sender, ResponseReceivedEventArgs e)
        {
            ProcessSearchResponseMessage(e.Message, e.LocalIPAddress);
        }

        private void CommsServer_RequestReceived(object sender, RequestReceivedEventArgs e)
        {
            ProcessNotificationMessage(e.Message, e.ReceivedFrom.Address);
        }
    }
}
