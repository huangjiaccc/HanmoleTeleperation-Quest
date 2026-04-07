#include "sender.h"

namespace {
constexpr uint32_t MTU_SIZE = 1400;
constexpr size_t HEARTBEAT_PREFIX_LEN = 6;
constexpr std::array<uint8_t, HEARTBEAT_PREFIX_LEN> VR_PACKET_PREFIX = {0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF};
constexpr std::chrono::seconds HEARTBEAT_INTERVAL{3};

#pragma pack(push, 1)
struct PacketHeader {
    int timestamp;      // Timestamp in milliseconds
    int frameId;        // Unique package identifier
    int splitId;        // 当前分片所属的 NALU 索引
    int totalSplits;    // NALU 分片总数
    int fragmentId;     // 当前片段索引
    int totalFragments; // 当前 NALU 内片段总数
    int fragmentSize;   // 片段数据长度
    int testingId;      // RTT 计算用 ID
};
#pragma pack(pop)

static_assert(sizeof(PacketHeader) == sizeof(int) * 8, "PacketHeader layout mismatch");
constexpr uint32_t HEADER_SIZE = static_cast<uint32_t>(sizeof(PacketHeader));
static_assert(MTU_SIZE > HEADER_SIZE, "MTU size too small for packet header");
constexpr uint32_t MAX_PAYLOAD_SIZE = MTU_SIZE - HEADER_SIZE;
} // namespace


Sender::Sender(uint16_t remote_port,
               const std::string &macAddress,
               const std::string &server_ip,
               int local_port)
    : transceiver_(server_ip, remote_port, local_port),
      MacAddr(macAddress),
      server_ip_(server_ip) {
    send_timestamps_.fill(0);
    processingThread = std::thread(&Sender::sendPackets, this);
}

void Sender::sendPackets() {
    while (!is_shutdown) {
        const std::string DEVICE_ID = MacAddr;
        char json_buffer[256];
        std::snprintf(json_buffer,
                        sizeof(json_buffer),
                        "{\"user_email\":\"\",\"device_id\":\"%s\",\"token\":\"\"}",
                        DEVICE_ID.c_str());

        std::vector<uint8_t> data_to_send;
        data_to_send.insert(data_to_send.end(), VR_PACKET_PREFIX.begin(), VR_PACKET_PREFIX.end());
        data_to_send.insert(data_to_send.end(),
                            reinterpret_cast<const uint8_t *>(json_buffer),
                            reinterpret_cast<const uint8_t *>(json_buffer) + std::strlen(json_buffer));

        transceiver_.send(data_to_send.data(), data_to_send.size());
        std::this_thread::sleep_for(HEARTBEAT_INTERVAL);
    }
}

bool Sender::sendFragmentedPacket(const char *data, int length) {
    if (!data || length <= 0) {
        return false;
    }

    bool all_sent = true;
    int frameId = generateFrameId();

    const PacketizationMode mode = packetization_mode_.load();
    std::vector<std::vector<uint8_t>> nalus;
    int totalSplits = 1;
    if (mode == PacketizationMode::kAnnexB_Nalu) {
        nalus = split_nalu(reinterpret_cast<const uint8_t *>(data), length);
        totalSplits = static_cast<int>(nalus.size());
        if (totalSplits <= 0) {
            std::cerr << "frame data is empty" << std::endl;
            return false;
        }
    }

    uint32_t sendTime = getCurrentTimestamp();
    {
        std::lock_guard<std::mutex> lock(rtt_mutex_);
        send_timestamps_[frameId % FRAME_HISTORY_SIZE] = sendTime;
    }

    std::array<uint8_t, MTU_SIZE> packet_buffer{};

    auto transmit_segment = [&](const uint8_t *segment_ptr, int dataLength, int splitIndex) {
        if (!segment_ptr || dataLength <= 0) {
            return;
        }

        const int totalFragments =
            (dataLength + static_cast<int>(MAX_PAYLOAD_SIZE) - 1) / static_cast<int>(MAX_PAYLOAD_SIZE);
        int testingId;
        {
            std::lock_guard<std::mutex> lock(received_id_timestamp_mutex_);
            testingId = received_id_;
        }

        for (int i = 0; i < totalFragments; ++i) {
            int offset = i * static_cast<int>(MAX_PAYLOAD_SIZE);
            int fragmentSize = std::min<int>(dataLength - offset, static_cast<int>(MAX_PAYLOAD_SIZE));
            if (fragmentSize <= 0) {
                continue;
            }

            PacketHeader header{
                static_cast<int>(sendTime),
                frameId,
                splitIndex,
                totalSplits,
                i,
                totalFragments,
                fragmentSize,
                testingId,
            };

            std::memcpy(packet_buffer.data(), &header, sizeof(PacketHeader));
            std::memcpy(packet_buffer.data() + sizeof(PacketHeader),
                        segment_ptr + offset,
                        fragmentSize);

            size_t packet_size = sizeof(PacketHeader) + fragmentSize;
            if (!transceiver_.send(packet_buffer.data(), packet_size)) {
                all_sent = false;
            }
        }
    };

    if (mode == PacketizationMode::kAnnexB_Nalu) {
        int curIndex = 0;
        for (auto &segData : nalus) {
            const int dataLength = static_cast<int>(segData.size());
            if (dataLength <= 0) {
                ++curIndex;
                continue;
            }
            transmit_segment(segData.data(), dataLength, curIndex);
            ++curIndex;
        }
    } else {
        transmit_segment(reinterpret_cast<const uint8_t *>(data), length, 0);
    }
    return all_sent;
}


std::vector<std::vector<uint8_t>> Sender::split_nalu(const uint8_t *data, size_t size) {
    const uint8_t start_code[] = {0x00, 0x00, 0x00, 0x01};
    std::vector<std::vector<uint8_t>> nalus;

    size_t i = 0;
    size_t start = 0;

    while (i + 4 < size) {
        if (std::memcmp(&data[i], start_code, 4) == 0) {
            if (i > start) {
                nalus.emplace_back(data + start, data + i);
            }
            start = i;
            i += 4;
        } else {
            ++i;
        }
    }

    if (size > start) {
        nalus.emplace_back(data + start, data + size);
    }

    return nalus;
}

uint32_t Sender::getCurrentTimestamp() {
    auto now = std::chrono::system_clock::now();
    auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(now.time_since_epoch());
    return static_cast<uint32_t>(ms.count());
}

void Sender::setReceiveTimestamp(const int &id, const int &timestamp) {
    std::lock_guard<std::mutex> lock(received_id_timestamp_mutex_);
    received_id_ = id;
    received_timestamp_ms_ = timestamp;
    calculateRtt(id);
}

void Sender::calculateRtt(int acked_frame_id) {
    std::lock_guard<std::mutex> lock(rtt_mutex_);

    int idx = acked_frame_id % FRAME_HISTORY_SIZE;
    uint32_t sendTime = send_timestamps_[idx];

    if (sendTime > 0) {
        uint32_t now = getCurrentTimestamp();
        if (now >= sendTime) {
            double rtt = static_cast<double>(now - sendTime);
            if (rtt > 0 && rtt < 2000) {
                double current = video_rtt_ms_.load();
                if (current <= 0) {
                    video_rtt_ms_.store(rtt);
                } else {
                    double smoothed = RTT_EMA_ALPHA * rtt + (1.0 - RTT_EMA_ALPHA) * current;
                    video_rtt_ms_.store(smoothed);
                }
            }
        }
        send_timestamps_[idx] = 0;
    }
}

double Sender::getVideoRttMs() const {
    return video_rtt_ms_.load();
}

void Sender::setFrameId(int frame_id) {
    frame_counter_ = frame_id;
}

uint32_t Sender::generateFrameId() {
    return ++frame_counter_;
}

uint32_t Sender::getFrameId() {
    return frame_counter_;
}

void Sender::setPacketizationMode(PacketizationMode mode) {
    packetization_mode_.store(mode);
}

Sender::PacketizationMode Sender::getPacketizationMode() const {
    return packetization_mode_.load();
}

Sender::~Sender() {
    is_shutdown = true;
    if (processingThread.joinable()) {
        processingThread.join();
    }
}
