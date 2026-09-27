using System.Runtime.InteropServices;

namespace WinSMS.Services;

/// <summary>
/// Reads the current subscriber information from the classic Windows Mobile
/// Broadband (MBN) COM API. Microsoft still documents this API for desktop apps.
/// Functional IMbnInterface objects are deliberately re-enumerated on every call
/// because Microsoft warns that cached interface objects return stale data.
/// </summary>
public sealed class LegacyMbnSubscriberService
{
    private static readonly Guid MbnInterfaceManagerClsid =
        new("BDFEE05B-4418-11DD-90ED-001C257CCFF1");

    public IReadOnlyList<LegacyMbnSubscriberInfo> GetSubscribers()
    {
        var result = new List<LegacyMbnSubscriberInfo>();
        object? managerObject = null;

        try
        {
            var managerType = Type.GetTypeFromCLSID(
                MbnInterfaceManagerClsid,
                throwOnError: false);

            if (managerType == null)
            {
                result.Add(new LegacyMbnSubscriberInfo(
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    Array.Empty<string>(),
                    "Windows Mobile Broadband COM manager is not registered."));
                return result;
            }

            managerObject = Activator.CreateInstance(managerType);
            if (managerObject is not IMbnInterfaceManager manager)
            {
                result.Add(new LegacyMbnSubscriberInfo(
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    Array.Empty<string>(),
                    "Windows Mobile Broadband COM manager could not be opened."));
                return result;
            }

            IMbnInterface[] interfaces;
            try
            {
                interfaces = manager.GetInterfaces() ?? Array.Empty<IMbnInterface>();
            }
            catch (Exception ex)
            {
                result.Add(new LegacyMbnSubscriberInfo(
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    Array.Empty<string>(),
                    FormatException(ex)));
                return result;
            }

            foreach (var mbnInterface in interfaces)
            {
                if (mbnInterface == null)
                    continue;

                IMbnSubscriberInformation? subscriber = null;
                try
                {
                    var interfaceId = mbnInterface.Get_InterfaceID() ?? string.Empty;
                    subscriber = mbnInterface.GetSubscriberInformation();

                    if (subscriber == null)
                    {
                        result.Add(new LegacyMbnSubscriberInfo(
                            interfaceId,
                            string.Empty,
                            string.Empty,
                            Array.Empty<string>(),
                            "Subscriber information is not available."));
                        continue;
                    }

                    var subscriberId = subscriber.Get_SubscriberID() ?? string.Empty;
                    var simIccId = subscriber.Get_SimIccID() ?? string.Empty;
                    var numbers = (subscriber.Get_TelephoneNumbers()
                                   ?? Array.Empty<string>())
                        .Where(number => !string.IsNullOrWhiteSpace(number))
                        .Select(number => number.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    result.Add(new LegacyMbnSubscriberInfo(
                        interfaceId,
                        subscriberId,
                        simIccId,
                        numbers,
                        null));
                }
                catch (Exception ex)
                {
                    string interfaceId;
                    try { interfaceId = mbnInterface.Get_InterfaceID() ?? string.Empty; }
                    catch { interfaceId = string.Empty; }

                    result.Add(new LegacyMbnSubscriberInfo(
                        interfaceId,
                        string.Empty,
                        string.Empty,
                        Array.Empty<string>(),
                        FormatException(ex)));
                }
                finally
                {
                    if (subscriber != null && Marshal.IsComObject(subscriber))
                    {
                        try { Marshal.FinalReleaseComObject(subscriber); } catch { }
                    }

                    if (Marshal.IsComObject(mbnInterface))
                    {
                        try { Marshal.FinalReleaseComObject(mbnInterface); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result.Add(new LegacyMbnSubscriberInfo(
                string.Empty,
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                FormatException(ex)));
        }
        finally
        {
            if (managerObject != null && Marshal.IsComObject(managerObject))
            {
                try { Marshal.FinalReleaseComObject(managerObject); } catch { }
            }
        }

        return result;
    }

    private static string FormatException(Exception ex)
        => $"{ex.GetType().Name}, HRESULT 0x{ex.HResult:X8}: {ex.Message}";

    [ComImport]
    [Guid("DCBBBAB6-201B-4BBB-AAEE-338E368AF6FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMbnInterfaceManager
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        IMbnInterface GetInterface(
            [MarshalAs(UnmanagedType.LPWStr)] string interfaceId);

        [return: MarshalAs(
            UnmanagedType.SafeArray,
            SafeArraySubType = VarEnum.VT_UNKNOWN)]
        IMbnInterface[] GetInterfaces();
    }

    [ComImport]
    [Guid("DCBBBAB6-2001-4BBB-AAEE-338E368AF6FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMbnInterface
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        string Get_InterfaceID();

        // Slot 4 in the native vtable. We never call this method; it is declared
        // so GetSubscriberInformation remains at its documented vtable slot.
        void GetInterfaceCapability(IntPtr interfaceCaps);

        [return: MarshalAs(UnmanagedType.Interface)]
        IMbnSubscriberInformation GetSubscriberInformation();
    }

    [ComImport]
    [Guid("459ECC43-BCF5-11DC-A8A8-001321F1405F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMbnSubscriberInformation
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        string Get_SubscriberID();

        [return: MarshalAs(UnmanagedType.BStr)]
        string Get_SimIccID();

        [return: MarshalAs(
            UnmanagedType.SafeArray,
            SafeArraySubType = VarEnum.VT_BSTR)]
        string[] Get_TelephoneNumbers();
    }
}

public sealed record LegacyMbnSubscriberInfo(
    string InterfaceId,
    string SubscriberId,
    string SimIccId,
    IReadOnlyList<string> TelephoneNumbers,
    string? Error);
