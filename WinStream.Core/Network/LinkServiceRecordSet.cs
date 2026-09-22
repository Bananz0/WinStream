using System.Net;

namespace WinStream.Core.Network;

/// <summary>
/// The PTR/SRV/TXT/A record set for one advertised Link receiver, plus the pure
/// question-matching rules. Kept separate from sockets so it can be unit tested.
/// </summary>
internal sealed class LinkServiceRecordSet
{
    public const uint DefaultTimeToLiveSeconds = 120;

    private readonly IReadOnlyList<KeyValuePair<string, string>> _txt;
    private readonly IPAddress _address;
    private readonly ushort _mediaPort;

    public LinkServiceRecordSet(
        string instanceLabel,
        string hostLabel,
        IPAddress address,
        ushort mediaPort,
        IReadOnlyList<KeyValuePair<string, string>> txtEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostLabel);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(txtEntries);
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Link advertising needs an IPv4 address.", nameof(address));
        }

        InstanceName = $"{SanitizeLabel(instanceLabel)}.{LinkDeviceDiscovery.ServiceType}";
        HostName = $"{SanitizeLabel(hostLabel)}.local.";
        _address = address;
        _mediaPort = mediaPort;
        _txt = txtEntries;
    }

    public string ServiceName => LinkDeviceDiscovery.ServiceType;

    public string InstanceName { get; }

    public string HostName { get; }

    /// <summary>Full record set for unsolicited announcements and goodbyes.</summary>
    public List<DnsResourceRecord> Announcement(uint ttlSeconds = DefaultTimeToLiveSeconds) => new()
    {
        DnsResourceRecord.Ptr(ServiceName, InstanceName, ttlSeconds),
        DnsResourceRecord.Srv(InstanceName, HostName, _mediaPort, ttlSeconds),
        DnsResourceRecord.Txt(InstanceName, _txt, ttlSeconds),
        DnsResourceRecord.A(HostName, _address, ttlSeconds)
    };

    /// <summary>False when nothing in the query concerns this service.</summary>
    public bool TryAnswer(
        IReadOnlyList<DnsQuestion> questions,
        out List<DnsResourceRecord> answers,
        out List<DnsResourceRecord> additional,
        uint ttlSeconds = DefaultTimeToLiveSeconds)
    {
        answers = new List<DnsResourceRecord>();
        additional = new List<DnsResourceRecord>();
        var wantsService = false;

        foreach (var question in questions)
        {
            if (MdnsWire.NameEquals(question.Name, ServiceName) &&
                Wants(question.Type, DnsRecordType.Ptr))
            {
                Add(answers, DnsResourceRecord.Ptr(ServiceName, InstanceName, ttlSeconds));
                wantsService = true;
            }

            if (MdnsWire.NameEquals(question.Name, InstanceName))
            {
                if (Wants(question.Type, DnsRecordType.Srv))
                {
                    Add(answers, DnsResourceRecord.Srv(InstanceName, HostName, _mediaPort, ttlSeconds));
                    wantsService = true;
                }

                if (Wants(question.Type, DnsRecordType.Txt))
                {
                    Add(answers, DnsResourceRecord.Txt(InstanceName, _txt, ttlSeconds));
                    wantsService = true;
                }
            }

            if (MdnsWire.NameEquals(question.Name, HostName) &&
                Wants(question.Type, DnsRecordType.A))
            {
                Add(answers, DnsResourceRecord.A(HostName, _address, ttlSeconds));
            }
        }

        if (wantsService)
        {
            // Saves the browser a follow-up query for the address it always needs next.
            foreach (var record in Announcement(ttlSeconds))
            {
                if (!Contains(answers, record))
                {
                    Add(additional, record);
                }
            }
        }

        return answers.Count > 0;
    }

    private static bool Wants(DnsRecordType asked, DnsRecordType offered) =>
        asked == offered || asked == DnsRecordType.Any;

    private static void Add(List<DnsResourceRecord> records, DnsResourceRecord record)
    {
        if (!Contains(records, record))
        {
            records.Add(record);
        }
    }

    private static bool Contains(List<DnsResourceRecord> records, DnsResourceRecord candidate) =>
        records.Exists(existing =>
            existing.Type == candidate.Type &&
            MdnsWire.NameEquals(existing.Name, candidate.Name));

    /// <summary>A DNS label cannot contain dots, and mDNS browsers dislike blanks.</summary>
    private static string SanitizeLabel(string label)
    {
        var cleaned = label.Trim().Replace('.', '-');
        return cleaned.Length > 63 ? cleaned[..63] : cleaned;
    }
}
