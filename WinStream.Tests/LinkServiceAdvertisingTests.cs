using System.Buffers.Binary;
using System.Net;
using System.Text;
using WinStream.Core.Network;
using WinStream.Core.Protocol.Link;

namespace WinStream.Tests;

public class MdnsWireTests
{
    [Fact]
    public void Question_names_and_types_are_read_from_a_query()
    {
        var query = Query(("_winstream-link._udp.local.", DnsRecordType.Ptr));

        Assert.True(MdnsWire.TryReadQuestions(query, out var questions));
        var question = Assert.Single(questions);
        Assert.Equal("_winstream-link._udp.local.", question.Name);
        Assert.Equal(DnsRecordType.Ptr, question.Type);
    }

    [Fact]
    public void Responses_are_ignored_so_we_never_answer_another_responder()
    {
        var response = Query(("_winstream-link._udp.local.", DnsRecordType.Ptr));
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 0x8400);

        Assert.False(MdnsWire.TryReadQuestions(response, out _));
    }

    [Fact]
    public void Compression_pointers_in_a_question_are_followed()
    {
        var query = QueryWithPointer();

        Assert.True(MdnsWire.TryReadQuestions(query, out var questions));
        Assert.Equal(2, questions.Count);
        Assert.Equal(questions[0].Name, questions[1].Name);
    }

    [Fact]
    public void A_pointer_loop_is_rejected_instead_of_hanging()
    {
        var query = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4), 1);
        query[12] = 0xC0;
        query[13] = 12; // points at itself

        Assert.False(MdnsWire.TryReadQuestions(query, out _));
    }

    [Fact]
    public void Truncated_input_is_rejected()
    {
        Assert.False(MdnsWire.TryReadQuestions(new byte[5], out _));

        var claimsAQuestion = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(claimsAQuestion.AsSpan(4), 1);
        Assert.False(MdnsWire.TryReadQuestions(claimsAQuestion, out _));
    }

    [Fact]
    public void Names_compare_with_or_without_the_trailing_dot()
    {
        Assert.True(MdnsWire.NameEquals("Rx._winstream-link._udp.local.", "rx._winstream-link._udp.local"));
        Assert.False(MdnsWire.NameEquals("rx._winstream-link._udp.local.", "rx._airplay._tcp.local."));
    }

    [Fact]
    public void Written_answers_are_readable_by_a_name_parser()
    {
        var records = new List<DnsResourceRecord>
        {
            DnsResourceRecord.Ptr("_winstream-link._udp.local.", "rx._winstream-link._udp.local.", 120)
        };
        var buffer = new byte[MdnsWire.MaxMessageBytes];

        var written = MdnsWire.WriteResponse(buffer, records, Array.Empty<DnsResourceRecord>());

        Assert.Equal(0x8400, BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(6)));
        Assert.Contains("_winstream-link", Encoding.UTF8.GetString(buffer, 0, written), StringComparison.Ordinal);
    }

    [Fact]
    public void Srv_records_carry_priority_weight_port_and_target()
    {
        var record = DnsResourceRecord.Srv("rx._winstream-link._udp.local.", "pi.local.", 47200, 120);

        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(record.Data));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(record.Data.AsSpan(2)));
        Assert.Equal(47200, BinaryPrimitives.ReadUInt16BigEndian(record.Data.AsSpan(4)));
        Assert.Equal(MdnsWire.EncodeName("pi.local."), record.Data[6..]);
    }

    [Fact]
    public void Txt_records_use_length_prefixed_key_value_chunks()
    {
        var record = DnsResourceRecord.Txt(
            "rx._winstream-link._udp.local.",
            new[] { new KeyValuePair<string, string>("ver", "1") },
            120);

        Assert.Equal((byte)5, record.Data[0]);
        Assert.Equal("ver=1", Encoding.UTF8.GetString(record.Data, 1, 5));
    }

    [Fact]
    public void Shared_ptr_records_never_set_the_cache_flush_bit()
    {
        var ptr = DnsResourceRecord.Ptr("_winstream-link._udp.local.", "rx._winstream-link._udp.local.", 120);
        var srv = DnsResourceRecord.Srv("rx._winstream-link._udp.local.", "pi.local.", 47200, 120);

        Assert.False(ptr.CacheFlush);
        Assert.True(srv.CacheFlush);
    }

    [Fact]
    public void Oversized_labels_are_rejected_rather_than_truncated()
    {
        Assert.Throws<ArgumentException>(() => MdnsWire.EncodeName(new string('a', 64) + ".local."));
    }

    private static byte[] Query(params (string Name, DnsRecordType Type)[] questions)
    {
        var body = new List<byte>();
        foreach (var (name, type) in questions)
        {
            body.AddRange(MdnsWire.EncodeName(name));
            var tail = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(tail, (ushort)type);
            BinaryPrimitives.WriteUInt16BigEndian(tail.AsSpan(2), 1);
            body.AddRange(tail);
        }

        var message = new byte[12 + body.Count];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), (ushort)questions.Length);
        body.CopyTo(message, 12);
        return message;
    }

    private static byte[] QueryWithPointer()
    {
        var name = MdnsWire.EncodeName("_winstream-link._udp.local.");
        var message = new byte[12 + name.Length + 4 + 2 + 4];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), 2);

        var offset = 12;
        name.CopyTo(message, offset);
        offset += name.Length;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset), (ushort)DnsRecordType.Ptr);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset + 2), 1);
        offset += 4;

        message[offset] = 0xC0;
        message[offset + 1] = 12;
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset), (ushort)DnsRecordType.Ptr);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset + 2), 1);
        return message;
    }
}

public class LinkServiceRecordSetTests
{
    private static LinkServiceRecordSet Create(string label = "Lab Pi") => new(
        label,
        "linkrx",
        IPAddress.Parse("192.168.1.60"),
        Wsl1Constants.DefaultMediaPort,
        new[] { new KeyValuePair<string, string>("ver", "1") });

    [Fact]
    public void Instance_and_host_names_are_fully_qualified()
    {
        var records = Create();

        Assert.Equal("Lab Pi._winstream-link._udp.local.", records.InstanceName);
        Assert.Equal("linkrx.local.", records.HostName);
        Assert.Equal(LinkDeviceDiscovery.ServiceType, records.ServiceName);
    }

    [Fact]
    public void Dots_in_a_display_name_cannot_forge_extra_labels()
    {
        Assert.Equal(
            "living-room-2._winstream-link._udp.local.",
            Create("living.room.2").InstanceName);
    }

    [Fact]
    public void Service_browse_is_answered_with_ptr_plus_the_rest_of_the_set()
    {
        var records = Create();

        Assert.True(records.TryAnswer(
            new[] { new DnsQuestion(LinkDeviceDiscovery.ServiceType, DnsRecordType.Ptr, false) },
            out var answers,
            out var additional));

        Assert.Equal(DnsRecordType.Ptr, Assert.Single(answers).Type);
        Assert.Equal(
            new[] { DnsRecordType.Srv, DnsRecordType.Txt, DnsRecordType.A },
            additional.Select(record => record.Type));
    }

    [Fact]
    public void Instance_queries_are_answered_without_duplicating_records()
    {
        var records = Create();

        Assert.True(records.TryAnswer(
            new[] { new DnsQuestion(records.InstanceName, DnsRecordType.Any, false) },
            out var answers,
            out var additional));

        Assert.Equal(
            new[] { DnsRecordType.Srv, DnsRecordType.Txt },
            answers.Select(record => record.Type));
        Assert.DoesNotContain(additional, record => record.Type == DnsRecordType.Srv);
    }

    [Fact]
    public void Host_lookups_return_only_the_address()
    {
        var records = Create();

        Assert.True(records.TryAnswer(
            new[] { new DnsQuestion("linkrx.local.", DnsRecordType.A, false) },
            out var answers,
            out var additional));

        Assert.Equal(DnsRecordType.A, Assert.Single(answers).Type);
        Assert.Empty(additional);
    }

    [Fact]
    public void Other_services_are_never_answered()
    {
        var records = Create();

        Assert.False(records.TryAnswer(
            new[]
            {
                new DnsQuestion("_raop._tcp.local.", DnsRecordType.Ptr, false),
                new DnsQuestion("_airplay._tcp.local.", DnsRecordType.Any, false)
            },
            out var answers,
            out _));

        Assert.Empty(answers);
    }

    [Fact]
    public void Goodbye_uses_a_zero_ttl_across_the_whole_set()
    {
        Assert.All(Create().Announcement(ttlSeconds: 0), record => Assert.Equal(0u, record.TimeToLiveSeconds));
    }

    [Fact]
    public void Ipv6_cannot_be_advertised_as_an_A_record()
    {
        Assert.Throws<ArgumentException>(() => new LinkServiceRecordSet(
            "rx",
            "linkrx",
            IPAddress.IPv6Loopback,
            Wsl1Constants.DefaultMediaPort,
            Array.Empty<KeyValuePair<string, string>>()));
    }
}
