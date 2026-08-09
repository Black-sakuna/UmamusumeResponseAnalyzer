using Gallop;
using MessagePack;
using MessagePack.Resolvers;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests
{
    /// <summary>
    /// Tier 2：真实单人模式响应能反序列化进 Gallop 强类型模型（与插件一致走 MessagePack）。
    /// </summary>
    public class GallopModelTests
    {
        [Fact]
        public void ResponseCorpus_HasNoUnresolvedEndpoints()
        {
            var unresolved = PacketCorpus.UnresolvedResponseEndpointPackets;

            Assert.Empty(unresolved);
        }

        [Theory]
        [MemberData(nameof(PacketCorpus.ResponseDescriptorCases), MemberType = typeof(PacketCorpus))]
        public void Response_DeserializesToCatalogModel(string? path, Type? responseType)
        {
            Assert.SkipWhen(path is null || responseType is null, "无带 canonical URL 的响应语料");

            var dto = MessagePackSerializer.Deserialize(responseType!, PacketCorpus.LoadBytes(path!));

            Assert.NotNull(dto);
        }

        [Theory]
        [MemberData(nameof(PacketCorpus.RequestDescriptorCases), MemberType = typeof(PacketCorpus))]
        public void Request_DeserializesToCatalogModel(string? path, Type? requestType)
        {
            Assert.SkipWhen(path is null || requestType is null, "无带 canonical URL 的请求语料");

            var dto = MessagePackSerializer.Deserialize(requestType!, PacketCorpus.LoadBytes(path!));

            Assert.NotNull(dto);
        }

        [Theory]
        [MemberData(nameof(PacketCorpus.SingleModeCases), MemberType = typeof(PacketCorpus))]
        public void SingleModeCheckEventResponse_DeserializesToModel(string? path)
        {
            Assert.SkipWhen(path is null, "无 SingleModeCheckEventResponse 语料");

            var resp = MessagePackSerializer.Deserialize<SingleModeCheckEventResponse>(PacketCorpus.LoadBytes(path!));

            Assert.NotNull(resp);
            Assert.NotNull(resp!.data);
            Assert.NotNull(resp.data.chara_info);
            // card_id 被成功映射(非 0)即证明 string-key → 字段名 的反序列化链路正确
            Assert.True(resp.data.chara_info.card_id > 0, "card_id 应被正确反序列化");
        }

        [Fact]
        public void MapNilValue_LeavesValueTypeFieldDefault()
        {
            var payload = MessagePackSerializer.Serialize(
                new Dictionary<string, object?> { ["special_home_id"] = null },
                ContractlessStandardResolver.Options);

            var dto = MessagePackSerializer.Deserialize<SingleModeHomeInfo>(payload);

            Assert.NotNull(dto);
            Assert.Equal(0, dto!.special_home_id);
        }

        [Fact]
        public void RootNil_DeserializesReferenceAsNull()
        {
            var dto = MessagePackSerializer.Deserialize<SingleModeHomeInfo>(new byte[] { MessagePackCode.Nil });

            Assert.Null(dto);
        }

        [Fact]
        public void EmptyArrayObjectValue_DeserializesReferenceFieldAsNull()
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            writer.WriteMapHeader(1);
            writer.Write("start_dress_info");
            writer.WriteArrayHeader(0);
            writer.Flush();

            var dto = MessagePackSerializer.Deserialize<SingleModeLoadCommon>(buffer.WrittenMemory);

            Assert.NotNull(dto);
            Assert.Null(dto!.start_dress_info);
        }

        [Theory]
        [InlineData("00", false)]
        [InlineData("01", true)]
        [InlineData("C2", false)]
        [InlineData("C3", true)]
        public void BoolField_DeserializesGameWireEncodings(string hex, bool expected)
        {
            var dto = MessagePackSerializer.Deserialize<StoryEventCharaBonus>(BoolFieldPayload(hex));

            Assert.NotNull(dto);
            Assert.Equal(expected, dto!.is_rental);
        }

        [Theory]
        [InlineData("02")]
        [InlineData("CC00")]
        [InlineData("D001")]
        public void BoolField_RejectsUnsupportedIntegerEncodings(string hex)
        {
            Assert.Throws<MessagePackSerializationException>(() =>
                MessagePackSerializer.Deserialize<StoryEventCharaBonus>(BoolFieldPayload(hex)));
        }

        [Theory]
        [InlineData(false, MessagePackCode.False)]
        [InlineData(true, MessagePackCode.True)]
        public void BoolField_SerializesAsMessagePackBoolean(bool value, byte expectedCode)
        {
            var payload = MessagePackSerializer.Serialize(new StoryEventCharaBonus { is_rental = value });

            Assert.Equal(expectedCode, ReadFieldCode(payload, "is_rental"));
        }

        [Fact]
        public void LegendLoadPacket_DeserializesNumericAndBooleanFields()
        {
            var path = Environment.GetEnvironmentVariable("URA_LEGEND_LOAD_PACKET");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(path), "未配置固定 Legend Load fixture 的 URA_LEGEND_LOAD_PACKET");
            Assert.True(File.Exists(path), $"URA_LEGEND_LOAD_PACKET 指向的文件不存在: {path}");
            var payload = File.ReadAllBytes(path!);
            Assert.Equal(
                "C643813F7F99D3261B0623B2E708E3D7A40F8CC35F9F595012FAAA209AB8853E",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)));

            var dto = MessagePackSerializer.Deserialize<SingleModeLegendLoadResponse>(payload);

            Assert.NotNull(dto);
            Assert.NotNull(dto!.data);
            Assert.NotNull(dto.data.single_mode_load_common);
            Assert.NotNull(dto.data.single_mode_load_common.story_event_chara_bonus_list);
            Assert.Equal(4, dto.data.single_mode_load_common.story_event_chara_bonus_list.Length);
            Assert.All(dto.data.single_mode_load_common.story_event_chara_bonus_list, bonus => Assert.False(bonus.is_rental));
            Assert.True(dto.data.single_mode_load_common.is_umaplan);
            Assert.NotNull(dto.data.legend_data_set);
            Assert.False(dto.data.legend_data_set.is_appear_legend);
        }

        private static byte[] BoolFieldPayload(string hex)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            writer.WriteMapHeader(1);
            writer.Write("is_rental");
            writer.WriteRaw(Convert.FromHexString(hex));
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        private static byte ReadFieldCode(byte[] payload, string fieldName)
        {
            var reader = new MessagePackReader(payload.AsMemory());
            var count = reader.ReadMapHeader();
            for (var i = 0; i < count; i++)
            {
                var key = reader.ReadString();
                if (key == fieldName)
                    return reader.NextCode;

                reader.Skip();
            }

            throw new InvalidOperationException($"Serialized payload did not contain {fieldName}.");
        }
    }
}
