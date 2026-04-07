#include "gstreamer_encoder.h"

#include <cstring>
#include <iostream>
#include <sstream>
#include <utility>

namespace {
constexpr int kEncodedWaitTimeoutMs = 30;
// Default color info for camera-style video sources (BT.709 limited).
constexpr int kColorStandard = 1; // bt709
constexpr int kColorRange = 2; // limited
constexpr int kColorTransfer = 3; // bt709
constexpr const char* kColorimetryCaps = "bt709";
constexpr const char* kRangeCaps = "limited";

bool is_valid_dimension(int value) {
    return value > 0;
}
} // namespace

GStreamerEncoder::GStreamerEncoder()
    : pipeline_(nullptr),
      appsrc_(nullptr),
      appsink_(nullptr),
      width_(0),
      height_(0),
      fps_(60),
      bitrate_(4'000'000),
      initialized_(false),
      frame_count_(0),
      input_format_(InputFormat::NV12) {
    if (!gst_is_initialized()) {
        gst_init(nullptr, nullptr);
    }
}

GStreamerEncoder::~GStreamerEncoder() {
    cleanup();
}

bool GStreamerEncoder::initialize(int width, int height, int fps, int bitrate, const std::string& codec_name) {
    cleanup();

    if (!is_valid_dimension(width) || !is_valid_dimension(height) || fps <= 0 || bitrate <= 0) {
        std::cerr << "[GStreamer] Invalid encoder parameters: "
                  << width << "x" << height << "@" << fps << " fps, bitrate=" << bitrate << std::endl;
        return false;
    }

    width_ = width;
    height_ = height;
    fps_ = fps;
    bitrate_ = bitrate;
    codec_name_ = codec_name;
    std::transform(codec_name_.begin(), codec_name_.end(), codec_name_.begin(), ::tolower);
    if (codec_name_ == "hevc") {
        codec_name_ = "h265";
    }

    input_format_ = InputFormat::NV12;  // 确保硬件编码器直接接收 NV12，减少额外格式转换
    frame_count_ = 0;

    std::cout << "[GStreamer] Color info (assumed): standard=" << kColorStandard
              << " range=" << kColorRange
              << " transfer=" << kColorTransfer
              << " (caps colorimetry=" << kColorimetryCaps
              << " range=" << kRangeCaps << ")" << std::endl;

    std::cout << "[GStreamer] Initializing hardware encoder: "
              << width_ << "x" << height_ << "@" << fps_
              << "fps, bitrate=" << bitrate_ << " bps, codec=" << codec_name_ << std::endl;

    auto candidates = build_pipeline_candidates();
    if (candidates.empty()) {
        std::cerr << "[GStreamer] No hardware encoder plugins available for codec " << codec_name_ << std::endl;
        return false;
    }

    for (const auto& candidate : candidates) {
        std::cout << "[GStreamer] Trying pipeline (" << candidate.label << ")" << std::endl;
        if (create_pipeline(candidate.description, candidate.encoder_element)) {
            active_pipeline_label_ = candidate.label;
            std::cout << "[GStreamer] Encoder initialized with pipeline: " << candidate.label << std::endl;
            return true;
        }
        std::cerr << "[GStreamer] Pipeline failed: " << candidate.label << std::endl;
    }

    std::cerr << "[GStreamer] Failed to initialise encoder for codec " << codec_name_ << std::endl;
    return false;
}

std::vector<GStreamerEncoder::PipelineCandidate> GStreamerEncoder::build_pipeline_candidates() const {
    std::vector<PipelineCandidate> candidates;

    const std::string format_caps = (input_format_ == InputFormat::NV12) ? "NV12" : "I420";
    const std::string color_caps =
        std::string("colorimetry=") + kColorimetryCaps + ",range=" + kRangeCaps + ",";
    const size_t max_bytes = static_cast<size_t>(width_) * height_ * 6;
    const std::string appsrc_caps =
        "appsrc name=src format=time is-live=true do-timestamp=true block=true "
        "max-bytes=" + std::to_string(max_bytes) + " "
        "caps=video/x-raw,format=" + format_caps +
        ",width=" + std::to_string(width_) +
        ",height=" + std::to_string(height_) +
        ",framerate=" + std::to_string(fps_) + "/1,"
        "pixel-aspect-ratio=1/1,interlace-mode=progressive," +
        color_caps +
        " ! "
        "queue max-size-buffers=12 leaky=downstream ! ";

    const std::string post_encoder_queue = "queue max-size-buffers=12 leaky=downstream ! ";
    const std::string appsink_desc =
        "appsink name=sink emit-signals=true sync=false max-buffers=8 drop=false";
    const std::string encoded_color_caps =
        std::string("colorimetry=") + kColorimetryCaps + ",range=" + kRangeCaps;

    auto add_candidate = [&](const std::string& label,
                             const std::string& encoder_element,
                             const std::string& parser_branch,
                             const std::string& pre_encoder_chain) {
        if (!is_element_available(encoder_element)) {
            return;
        }
        PipelineCandidate candidate;
        candidate.label = label;
        candidate.encoder_element = encoder_element;
        candidate.description =
            appsrc_caps +
            pre_encoder_chain +
            encoder_element + " name=hwenc ! " +
            post_encoder_queue +
            parser_branch +
            appsink_desc;
        candidates.emplace_back(std::move(candidate));
    };
    const std::string va_preprocess =
        (input_format_ == InputFormat::NV12)
            ? ""
            : "videoconvert n-threads=2 ! "
              "vaapipostproc format=nv12 ! ";

    if (codec_name_ == "h264") {
        const std::string parser_branch =
            "h264parse config-interval=1 disable-passthrough=true ! "
            "video/x-h264,stream-format=byte-stream,alignment=au," + encoded_color_caps + " ! ";
        add_candidate("NVENC H.264", "nvh264enc", parser_branch, "");
        add_candidate("VA H.264 (vah264enc)", "vah264enc", parser_branch, va_preprocess);
        add_candidate("VAAPI H.264", "vaapih264enc", parser_branch, va_preprocess);
    } else if (codec_name_ == "h265") {
        const std::string parser_branch =
            "h265parse config-interval=1 disable-passthrough=true ! "
            "video/x-h265,stream-format=byte-stream,alignment=au," + encoded_color_caps + " ! ";
        add_candidate("NVENC H.265", "nvh265enc", parser_branch, "");
        add_candidate("VA H.265 (vah265enc)", "vah265enc", parser_branch, va_preprocess);
        add_candidate("VAAPI H.265", "vaapih265enc", parser_branch, va_preprocess);
    } else if (codec_name_ == "av1") {
        const std::string parser_branch =
            "av1parse ! "
            "video/x-av1,stream-format=obu-stream,alignment=tu," + encoded_color_caps + " ! ";
        add_candidate("VA AV1 (vaav1enc)", "vaav1enc", parser_branch, va_preprocess);
    }

    return candidates;
}

bool GStreamerEncoder::create_pipeline(const std::string& pipeline_description,
                                       const std::string& encoder_element) {
    cleanup();

    GError* error = nullptr;
    GstElement* pipeline = gst_parse_launch(pipeline_description.c_str(), &error);
    if (error) {
        std::cerr << "[GStreamer] Failed to create pipeline: " << error->message << std::endl;
        g_error_free(error);
        return false;
    }
    if (!pipeline) {
        std::cerr << "[GStreamer] Failed to create pipeline (nullptr)" << std::endl;
        return false;
    }

    GstElement* appsrc = gst_bin_get_by_name(GST_BIN(pipeline), "src");
    GstElement* appsink = gst_bin_get_by_name(GST_BIN(pipeline), "sink");
    GstElement* encoder = gst_bin_get_by_name(GST_BIN(pipeline), "hwenc");

    if (!appsrc || !appsink || !encoder) {
        std::cerr << "[GStreamer] Missing appsrc/appsink/encoder elements in pipeline" << std::endl;
        if (appsrc) gst_object_unref(appsrc);
        if (appsink) gst_object_unref(appsink);
        if (encoder) gst_object_unref(encoder);
        gst_object_unref(pipeline);
        return false;
    }

    GstAppSinkCallbacks callbacks = {nullptr, nullptr, on_new_sample_static, nullptr};
    gst_app_sink_set_callbacks(GST_APP_SINK(appsink), &callbacks, this, nullptr);
    configure_encoder_properties(encoder);

    GstStateChangeReturn ret = gst_element_set_state(pipeline, GST_STATE_PLAYING);
    if (ret == GST_STATE_CHANGE_FAILURE) {
        std::cerr << "[GStreamer] Failed to set pipeline to PLAYING state" << std::endl;
        gst_object_unref(appsrc);
        gst_object_unref(appsink);
        gst_object_unref(encoder);
        gst_object_unref(pipeline);
        return false;
    }

    pipeline_ = pipeline;
    appsrc_ = appsrc;
    appsink_ = appsink;
    active_encoder_element_ = encoder_element;
    initialized_ = true;
    frame_count_ = 0;

    gst_object_unref(encoder);
    flush_encoded_queue();
    return true;
}

int GStreamerEncoder::encode(const uint8_t* yuv_data, size_t yuv_size, std::vector<uint8_t>& output) {
    if (!initialized_ || !yuv_data) {
        return -1;
    }
    const size_t expected_i420 = static_cast<size_t>(width_) * height_ * 3 / 2;
    if (yuv_size < expected_i420) {
        std::cerr << "[GStreamer] Insufficient I420 data: expected " << expected_i420
                  << ", got " << yuv_size << std::endl;
        return -1;
    }

    const size_t padded_size = expected_frame_size();
    if (input_format_ == InputFormat::NV12) {
        conversion_buffer_.resize(padded_size);
        convert_i420_to_nv12(yuv_data, conversion_buffer_.data());
        int result = push_frame_to_encoder(conversion_buffer_.data(), padded_size, output);
        if (result > 0) {
            static int encode_success_log = 0;
            if (encode_success_log < 5) {
                std::cout << "[GStreamer] encode produced " << result << " bytes" << std::endl;
                ++encode_success_log;
            }
        } else if (result == 0) {
            static int encode_pending_log = 0;
            if (encode_pending_log < 5) {
                std::cout << "[GStreamer] encode pending, no encoded data yet" << std::endl;
                ++encode_pending_log;
            }
        } else {
            std::cerr << "[GStreamer] encode failed for current frame" << std::endl;
        }
        return result;
    }
    int result = push_frame_to_encoder(yuv_data, padded_size, output);
    if (result > 0) {
        static int encode_success_log = 0;
        if (encode_success_log < 5) {
            std::cout << "[GStreamer] encode produced " << result << " bytes" << std::endl;
            ++encode_success_log;
        }
    } else if (result == 0) {
        static int encode_pending_log = 0;
        if (encode_pending_log < 5) {
            std::cout << "[GStreamer] encode pending, no encoded data yet" << std::endl;
            ++encode_pending_log;
        }
    } else {
        std::cerr << "[GStreamer] encode failed for current frame" << std::endl;
    }
    return result;
}

int GStreamerEncoder::encodeNV12(const uint8_t* nv12_data, size_t nv12_size, std::vector<uint8_t>& output) {
    if (!initialized_ || !nv12_data) {
        return -1;
    }
    const size_t expected = expected_frame_size();
    if (nv12_size < expected) {
        std::cerr << "[GStreamer] Insufficient NV12 data: expected " << expected << ", got " << nv12_size << std::endl;
        return -1;
    }

    if (input_format_ == InputFormat::I420) {
        conversion_buffer_.resize(expected);
        convert_nv12_to_i420(nv12_data, conversion_buffer_.data());
        return push_frame_to_encoder(conversion_buffer_.data(), expected, output);
    }
    return push_frame_to_encoder(nv12_data, expected, output);
}

int GStreamerEncoder::get_encoded_data(std::vector<uint8_t>& output) {
    std::lock_guard<std::mutex> lock(queue_mutex_);
    if (encoded_buffers_.empty()) {
        return 0;
    }
    output = std::move(encoded_buffers_.front());
    encoded_buffers_.pop();
    return static_cast<int>(output.size());
}

bool GStreamerEncoder::set_bitrate(int bitrate) {
    if (!initialized_ || bitrate <= 0) {
        return false;
    }
    bitrate_ = bitrate;

    GstElement* encoder = gst_bin_get_by_name(GST_BIN(pipeline_), "hwenc");
    if (!encoder) {
        return false;
    }
    configure_encoder_properties(encoder);
    gst_object_unref(encoder);
    return true;
}

bool GStreamerEncoder::is_initialized() const {
    return initialized_;
}

bool GStreamerEncoder::push_frame_to_encoder(const uint8_t* data,
                                             size_t data_size,
                                             std::vector<uint8_t>& output) {
    if (!appsrc_) {
        return -1;
    }

    GstBuffer* buffer = gst_buffer_new_allocate(nullptr, data_size, nullptr);
    if (!buffer) {
        return -1;
    }

    GstMapInfo map;
    if (!gst_buffer_map(buffer, &map, GST_MAP_WRITE)) {
        gst_buffer_unref(buffer);
        return -1;
    }
    std::memcpy(map.data, data, data_size);
    gst_buffer_unmap(buffer, &map);

    const gint64 pts = (frame_count_ * GST_SECOND) / fps_;
    GST_BUFFER_PTS(buffer) = pts;
    GST_BUFFER_DTS(buffer) = pts;
    GST_BUFFER_DURATION(buffer) = GST_SECOND / fps_;
    frame_count_++;

    GstFlowReturn ret = gst_app_src_push_buffer(GST_APP_SRC(appsrc_), buffer);
    if (ret != GST_FLOW_OK) {
        std::cerr << "[GStreamer] Failed to push buffer into appsrc: " << ret << std::endl;
        return -1;
    }

    std::unique_lock<std::mutex> lock(queue_mutex_);
    if (!encoded_queue_.wait_for(
            lock, std::chrono::milliseconds(kEncodedWaitTimeoutMs),
            [this]() { return !encoded_buffers_.empty(); })) {
        static int wait_log_counter = 0;
        if (wait_log_counter < 5) {
            std::cout << "[GStreamer] Waiting for encoded data timed out (no buffers yet)" << std::endl;
            ++wait_log_counter;
        }
        return 0;
    }

    output = std::move(encoded_buffers_.front());
    encoded_buffers_.pop();
    return static_cast<int>(output.size());
}

void GStreamerEncoder::configure_encoder_properties(GstElement* encoder) {
    if (!encoder) {
        return;
    }

    const gint bitrate_kbps = std::max(1, bitrate_ / 1000);
    set_int_property(encoder, "bitrate", bitrate_kbps);
    set_int_property(encoder, "target-bitrate", bitrate_kbps);
    set_int_property(encoder, "max-bitrate", bitrate_kbps);

    const gint gop = std::max(1, fps_);
    set_int_property(encoder, "iframeinterval", gop);
    set_int_property(encoder, "keyframe-period", gop);
    set_int_property(encoder, "gop-size", gop);
    set_int_property(encoder, "key-int-max", gop);
}

bool GStreamerEncoder::set_int_property(GstElement* encoder, const char* name, gint64 value) {
    if (!encoder || !name) {
        return false;
    }
    GParamSpec* spec = g_object_class_find_property(G_OBJECT_GET_CLASS(encoder), name);
    if (!spec) {
        return false;
    }
    GValue gvalue = G_VALUE_INIT;
    g_value_init(&gvalue, spec->value_type);
    if (G_VALUE_HOLDS_INT(&gvalue)) {
        g_value_set_int(&gvalue, static_cast<gint>(value));
    } else if (G_VALUE_HOLDS_UINT(&gvalue)) {
        g_value_set_uint(&gvalue, static_cast<guint>(std::max<gint64>(0, value)));
    } else if (G_VALUE_HOLDS_UINT64(&gvalue)) {
        g_value_set_uint64(&gvalue, static_cast<guint64>(value));
    } else if (G_VALUE_HOLDS_INT64(&gvalue)) {
        g_value_set_int64(&gvalue, value);
    } else {
        g_value_unset(&gvalue);
        return false;
    }
    g_object_set_property(G_OBJECT(encoder), name, &gvalue);
    g_value_unset(&gvalue);
    return true;
}

int GStreamerEncoder::aligned_width() const {
    return (width_ + 3) & ~3;
}

size_t GStreamerEncoder::expected_frame_size() const {
    const int stride = aligned_width();
    return static_cast<size_t>(stride) * height_ * 3 / 2;
}

void GStreamerEncoder::flush_encoded_queue() {
    std::lock_guard<std::mutex> lock(queue_mutex_);
    std::queue<std::vector<uint8_t>> empty;
    std::swap(encoded_buffers_, empty);
}

bool GStreamerEncoder::is_element_available(const std::string& element_name) const {
    if (element_name.empty()) {
        return false;
    }
    GstElementFactory* factory = gst_element_factory_find(element_name.c_str());
    if (!factory) {
        return false;
    }
    gst_object_unref(factory);
    return true;
}

void GStreamerEncoder::convert_i420_to_nv12(const uint8_t* src, uint8_t* dst) const {
    if (!src || !dst) {
        return;
    }

    const int stride = aligned_width();
    const int uv_stride = stride;
    const int uv_height = height_ / 2;
    const int uv_width = width_ / 2;

    const uint8_t* src_y = src;
    uint8_t* dst_y = dst;
    for (int row = 0; row < height_; ++row) {
        std::memcpy(dst_y + static_cast<size_t>(row) * stride,
                    src_y + static_cast<size_t>(row) * width_,
                    width_);
        if (stride > width_) {
            std::memset(dst_y + static_cast<size_t>(row) * stride + width_, 0, stride - width_);
        }
    }

    const size_t luma_plane_bytes = static_cast<size_t>(stride) * height_;
    uint8_t* dst_uv = dst + luma_plane_bytes;

    const uint8_t* src_u = src + static_cast<size_t>(width_) * height_;
    const uint8_t* src_v = src_u + static_cast<size_t>(width_) * height_ / 4;

    for (int row = 0; row < uv_height; ++row) {
        uint8_t* dst_row = dst_uv + static_cast<size_t>(row) * uv_stride;
        const uint8_t* src_u_row = src_u + static_cast<size_t>(row) * uv_width;
        const uint8_t* src_v_row = src_v + static_cast<size_t>(row) * uv_width;
        for (int col = 0; col < uv_width; ++col) {
            dst_row[col * 2] = src_u_row[col];
            dst_row[col * 2 + 1] = src_v_row[col];
        }
        if (uv_stride > uv_width * 2) {
            std::memset(dst_row + uv_width * 2, 0, uv_stride - uv_width * 2);
        }
    }
}

void GStreamerEncoder::convert_nv12_to_i420(const uint8_t* src, uint8_t* dst) const {
    if (!src || !dst) {
        return;
    }

    const int stride = aligned_width();
    const int uv_stride = stride;
    const int uv_height = height_ / 2;
    const int uv_width = width_ / 2;

    uint8_t* dst_y = dst;
    for (int row = 0; row < height_; ++row) {
        const uint8_t* src_row = src + static_cast<size_t>(row) * stride;
        std::memcpy(dst_y + static_cast<size_t>(row) * width_, src_row, width_);
    }

    const size_t luma_plane_bytes = static_cast<size_t>(stride) * height_;
    const uint8_t* src_uv = src + luma_plane_bytes;

    uint8_t* dst_u = dst + static_cast<size_t>(width_) * height_;
    uint8_t* dst_v = dst_u + static_cast<size_t>(width_) * height_ / 4;

    for (int row = 0; row < uv_height; ++row) {
        const uint8_t* src_row = src_uv + static_cast<size_t>(row) * uv_stride;
        uint8_t* dst_u_row = dst_u + static_cast<size_t>(row) * uv_width;
        uint8_t* dst_v_row = dst_v + static_cast<size_t>(row) * uv_width;
        for (int col = 0; col < uv_width; ++col) {
            dst_u_row[col] = src_row[col * 2];
            dst_v_row[col] = src_row[col * 2 + 1];
        }
    }
}

GstFlowReturn GStreamerEncoder::on_new_sample_static(GstAppSink* appsink, gpointer user_data) {
    auto* encoder = static_cast<GStreamerEncoder*>(user_data);
    return encoder->on_new_sample(appsink);
}

GstFlowReturn GStreamerEncoder::on_new_sample(GstAppSink* appsink) {
    GstSample* sample = gst_app_sink_pull_sample(appsink);
    if (!sample) {
        return GST_FLOW_ERROR;
    }

    GstBuffer* buffer = gst_sample_get_buffer(sample);
    if (buffer) {
        GstMapInfo map;
        if (gst_buffer_map(buffer, &map, GST_MAP_READ)) {
            std::vector<uint8_t> encoded(map.data, map.data + map.size);
            {
                std::lock_guard<std::mutex> lock(queue_mutex_);
                encoded_buffers_.push(std::move(encoded));
            }
            encoded_queue_.notify_one();
            gst_buffer_unmap(buffer, &map);
        }
    }

    gst_sample_unref(sample);
    return GST_FLOW_OK;
}

void GStreamerEncoder::cleanup() {
    if (pipeline_) {
        gst_element_set_state(pipeline_, GST_STATE_NULL);
        gst_object_unref(pipeline_);
        pipeline_ = nullptr;
    }
    if (appsrc_) {
        gst_object_unref(appsrc_);
        appsrc_ = nullptr;
    }
    if (appsink_) {
        gst_object_unref(appsink_);
        appsink_ = nullptr;
    }
    initialized_ = false;
    active_pipeline_label_.clear();
    active_encoder_element_.clear();
    flush_encoded_queue();
}
