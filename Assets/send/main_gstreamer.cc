#include <pybind11/pybind11.h>
#include <pybind11/numpy.h>

#include <iostream>
#include <fstream>
#include <memory>
#include <thread>
#include <mutex>
#include <vector>
#include <filesystem>
#include <chrono>
#include <iomanip>
#include <future>
#include <sstream>
#include <algorithm>
#include <ctime>
#include "sender.h"
#include "gstreamer_encoder.h"
#include "ros_video_publisher.h"
#include <opencv2/opencv.hpp>

namespace fs = std::filesystem;

namespace py = pybind11;

namespace nv_video_encode
{
    class NvVideoEncode
    {
    public:
        NvVideoEncode(const std::string &server_ip,
                      const std::string &macAddress,
                      const int port,
                      const int local_port = -1)
            : sender_(static_cast<uint16_t>(port), macAddress, server_ip, local_port),
              server_ip_(server_ip),
              port_(port),
              is_shutdown_(false),
              publish_av1_video_data_(false),
              encoder_(std::make_unique<GStreamerEncoder>())
        {
            std::string node_name = "hanmole_robot_video_" + getEnv("ROS_DOMAIN_ID", "001");
            std::string topic_name = "/bgr_image_frame";
            try
            {
                ros_video_pub_ = std::make_unique<RosVideoPublisher>(node_name, topic_name);
            }
            catch (const std::exception &e)
            {
                std::cerr << "[x86 GStreamer Hardware Encoder] Failed to initialise ROS video publisher: "
                          << e.what() << ". Continuing without ROS output." << std::endl;
                ros_video_pub_.reset();
            }
            catch (...)
            {
                std::cerr << "[x86 GStreamer Hardware Encoder] Unknown error initialising ROS video publisher. "
                          << "Continuing without ROS output." << std::endl;
                ros_video_pub_.reset();
            }

            if (ivf_file_.is_open())
            {
                ivf_file_.close();
            }
            save_frame_ = true;
            ivf_enabled_ = false;
            output_buffers_.clear();
            // Create a separate thread for the encoded frame retrieval
            sending_thread_ = std::thread(&NvVideoEncode::get_encoded_frame, this);
            std::cout << "[x86 GStreamer Hardware Encoder] Constructor" << std::endl;

        }

        ~NvVideoEncode()
        {
            std::cout << "[x86 GStreamer Hardware Encoder] Destructor" << std::endl;
            is_shutdown_ = true;

            if (sending_thread_.joinable())
            {
                sending_thread_.join();
            }

            output_buffers_.clear();
            encoder_.reset();
            ros_video_pub_.reset();
            std::cout << "[x86 GStreamer Hardware Encoder] Destructor completed" << std::endl;
        }

        bool initialise(int width, int height, int overlap, int fps, int bitrate, const std::string &encoder_type)
        {
            width_ = width;
            height_ = height;
            fps_ = fps;
            bitrate_ = bitrate;
            overlap_width = std::max(0, overlap);
            encoder_type_ = encoder_type == "hevc" ? "h265" : encoder_type;
            if (encoder_type_ == "av1")
            {
                sender_.setPacketizationMode(Sender::PacketizationMode::kWholeFrame);
            }
            else
            {
                sender_.setPacketizationMode(Sender::PacketizationMode::kAnnexB_Nalu);
            }

            std::cout << "[x86 GStreamer Hardware Encoder] Initializing: "
                      << width << "x" << height << "@" << fps << "fps, "
                      << bitrate << " bps, codec: " << encoder_type << std::endl;

            update_output_geometry();
            reported_shape_warning_ = false;

            bool success = encoder_->initialize(output_width_, height_, fps_, bitrate_, encoder_type_);

            if (success)
            {
                std::cout << "[x86 GStreamer Hardware Encoder] Initialization successful!" << std::endl;
            }
            else
            {
                std::cerr << "[x86 GStreamer Hardware Encoder] Initialization failed!" << std::endl;
            }

            reset_frame_id();
            return success;
        }

        // 获取当前时间作为文件名
        std::string generateTimestampFilename(const std::string& extension = "ivf") {
            auto now = std::chrono::system_clock::now();
            std::time_t t = std::chrono::system_clock::to_time_t(now);

            std::tm tm_buf;
            localtime_r(&t, &tm_buf);

            std::ostringstream oss;
            oss << std::put_time(&tm_buf, "%Y-%m-%d_%H-%M-%S") << "." << extension;
            return oss.str();
        }
        // 获取当前时间戳
        int generateTimestamp() {
            auto now = std::chrono::system_clock::now();
            // 转换为时间戳（毫秒）
            auto timestamp_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
            now.time_since_epoch()).count();
            return timestamp_ms;
        }

        void open_ivf_file()
        {
            if (ivf_enabled_)
            {
                return;
            }

            try
            {
                fs::path target_dir = fs::current_path() / "video_files";
                if (!fs::exists(target_dir))
                {
                    std::cout << "📂 创建目录: " << target_dir << std::endl;
                    fs::create_directories(target_dir);
                }

                fs::path file_path = target_dir / generateTimestampFilename("ivf");
                ivf_file_.open(file_path, std::ios::binary);
                if (!ivf_file_.is_open())
                {
                    std::cerr << "❌ 无法打开 IVF 文件: " << file_path << std::endl;
                    return;
                }

                write_ivf_header();
                ivf_enabled_ = true;
                ivf_frame_index_ = 0;
                std::cout << "✅ IVF 录制开启: " << file_path << std::endl;
            }
            catch (const std::exception &e)
            {
                std::cerr << "❌ 打开 IVF 文件异常: " << e.what() << std::endl;
                ivf_enabled_ = false;
            }
        }

        void write_ivf_header()
        {
            if (!ivf_file_.is_open())
            {
                return;
            }

            const char signature[4] = {'D', 'K', 'I', 'F'};
            ivf_file_.write(signature, sizeof(signature));

            uint16_t version = 0;
            uint16_t header_size = 32;
            ivf_file_.write(reinterpret_cast<const char *>(&version), sizeof(version));
            ivf_file_.write(reinterpret_cast<const char *>(&header_size), sizeof(header_size));

            if (encoder_type_ == "av1") {
                const char fourcc[4] = {'A', 'V', '0', '1'};
                ivf_file_.write(fourcc, sizeof(fourcc));
            } else if (encoder_type_ == "h264") {
                const char fourcc[4] = {'H', '2', '6', '4'};
                ivf_file_.write(fourcc, sizeof(fourcc));
            } else if (encoder_type_ == "h265") {
                const char fourcc[4] = {'H', '2', '6', '5'};
                ivf_file_.write(fourcc, sizeof(fourcc));
            }

            uint16_t width = static_cast<uint16_t>(output_width_);
            uint16_t height = static_cast<uint16_t>(height_);
            ivf_file_.write(reinterpret_cast<const char *>(&width), sizeof(width));
            ivf_file_.write(reinterpret_cast<const char *>(&height), sizeof(height));

            uint32_t timebase_den = static_cast<uint32_t>(fps_);
            uint32_t timebase_num = 1;
            ivf_file_.write(reinterpret_cast<const char *>(&timebase_den), sizeof(timebase_den));
            ivf_file_.write(reinterpret_cast<const char *>(&timebase_num), sizeof(timebase_num));

            uint32_t frame_count = 0;
            uint32_t unused = 0;
            ivf_file_.write(reinterpret_cast<const char *>(&frame_count), sizeof(frame_count));
            ivf_file_.write(reinterpret_cast<const char *>(&unused), sizeof(unused));
        }

        void write_ivf_frame(const std::vector<uint8_t> &data)
        {
            if (!ivf_enabled_ || !ivf_file_.is_open())
            {
                return;
            }

            uint32_t frame_size = static_cast<uint32_t>(data.size());
            uint64_t timestamp = ivf_frame_index_++;

            ivf_file_.write(reinterpret_cast<const char *>(&frame_size), sizeof(frame_size));
            ivf_file_.write(reinterpret_cast<const char *>(&timestamp), sizeof(timestamp));
            ivf_file_.write(reinterpret_cast<const char *>(data.data()), frame_size);
            ivf_file_.flush();
        }

        bool encode(py::array_t<uint8_t> yuv_frame)
        {
            if (!encoder_->is_initialized())
            {
                std::cerr << "[x86 GStreamer Hardware Encoder] Encoder not initialized!" << std::endl;
                return false;
            }
            
            py::buffer_info buf_info = yuv_frame.request();

            if (buf_info.ndim != 1)
            {
                std::cerr << "[x86 GStreamer Hardware Encoder] Expected 1D array!" << std::endl;
                return false;
            }

            size_t yuv_size = buf_info.shape[0];
            size_t expected_size = width_ * height_ * 3 / 2; // YUV420

            if (yuv_size < expected_size)
            {
                std::cerr << "[x86 GStreamer Hardware Encoder] Insufficient YUV data! Expected: " << "width: " << width_ << "height:" << height_
                          << expected_size << ", got: " << yuv_size << std::endl;
                return false;
            }

            const uint8_t *yuv_data = static_cast<const uint8_t *>(buf_info.ptr);

            std::vector<uint8_t> encoded_data;
            int encoded_size = encoder_->encode(yuv_data, yuv_size, encoded_data);

            if (encoded_size > 0)
            {
                if (save_frame_ && !ivf_enabled_)
                {
                    open_ivf_file();
                }
                    std::lock_guard<std::mutex> lock(output_buffers_mutex_);
                    output_buffers_.push_back(encoded_data);
                return true;
            }
            else if (encoded_size == 0)
            {
                // No data yet, but not an error
                return true;
            }

            return false;
        }

        bool blend_encode(py::array_t<uint8_t> left, py::array_t<uint8_t> right)
        {
            auto infoL = left.request();
            auto infoR = right.request();

            if (!validate_frame_shape(infoL, "left") || !validate_frame_shape(infoR, "right"))
            {
                return false;
            }

            cv::Mat imgL(height_, width_, CV_8UC3, infoL.ptr);
            cv::Mat imgR(height_, width_, CV_8UC3, infoR.ptr);

            ensure_frame_buffers();
            cv::Mat fused = stitched_frame_(cv::Rect(0, 0, output_width_, height_));

            const int overlap_px = effective_overlap_;
            const int keep_width = width_ - overlap_px;

            cv::Mat left_region = fused(cv::Rect(0, 0, keep_width, height_));
            imgL(cv::Rect(0, 0, keep_width, height_)).copyTo(left_region);

            cv::Mat right_region = fused(cv::Rect(keep_width + overlap_px, 0, keep_width, height_));
            imgR(cv::Rect(overlap_px, 0, keep_width, height_)).copyTo(right_region);

            if (overlap_px > 0)
            {
                cv::Mat overlap_target = fused(cv::Rect(keep_width, 0, overlap_px, height_));
                cv::Mat overlap_left = imgL(cv::Rect(keep_width, 0, overlap_px, height_));
                cv::Mat overlap_right = imgR(cv::Rect(0, 0, overlap_px, height_));
                blend_overlap_region(overlap_left, overlap_right, overlap_target);
            }

            if (ros_video_pub_)
            {
                try
                {
                    const size_t bgr_bytes = static_cast<size_t>(output_width_) * height_ * 3;
                    ros_video_pub_->publish(reinterpret_cast<char*>(fused.data), bgr_bytes, output_width_, height_);
                }
                catch (const std::exception &e)
                {
                    std::cerr << "[x86 GStreamer Hardware Encoder] Failed to send frame to ROS: " << e.what() << std::endl;
                }
            }

            maybe_dump_fused_frame(fused);
            cv::cvtColor(fused, yuv420_frame_, cv::COLOR_BGR2YUV_I420);
            const size_t yuv_size = fused_yuv_size();

            std::vector<uint8_t> encoded_data;
            const uint8_t* yuv_data = static_cast<const uint8_t*>(yuv420_frame_.data);
            int encoded_size = encoder_->encode(yuv_data, yuv_size, encoded_data);

            if (encoded_size > 0)
            {
                if (save_frame_ && !ivf_enabled_)
                {
                    open_ivf_file();
                }
                std::lock_guard<std::mutex> lock(output_buffers_mutex_);
                output_buffers_.push_back(std::move(encoded_data));
                return true;
            }
            else if (encoded_size == 0)
            {
                return true;
            }

            return false;
        }

        void get_encoded_frame()
        {
            std::cout << "[x86 GStreamer Hardware Encoder] Frame retrieval thread started" << std::endl;

            while (!is_shutdown_)
            {
                std::this_thread::sleep_for(std::chrono::milliseconds(5));

                // Check for any pending encoded data from GStreamer
                std::vector<uint8_t> pending_data;
                if (encoder_->get_encoded_data(pending_data) > 0)
                {
                    std::lock_guard<std::mutex> lock(output_buffers_mutex_);
                    output_buffers_.push_back(pending_data);
                }

                std::vector<std::vector<uint8_t>> frames_to_send;
                {
                    std::lock_guard<std::mutex> lock(output_buffers_mutex_);
                    frames_to_send.swap(output_buffers_);
                }

                for (const auto &frame_data : frames_to_send)
                {
                    if (!frame_data.empty())
                    {
                        // if (save_frame_ && ivf_enabled_)
                        // {
                        //     try
                        //     {
                        //         write_ivf_frame(frame_data);
                        //     }
                        //     catch (const std::exception &e)
                        //     {
                        //         std::cerr << "[x86 GStreamer Hardware Encoder] Failed to write IVF frame: " << e.what() << std::endl;
                        //         ivf_enabled_ = false;
                        //         if (ivf_file_.is_open())
                        //         {
                        //             ivf_file_.close();
                        //         }
                        //     }
                        // }

                        const char *data = reinterpret_cast<const char *>(frame_data.data());
                        static int log_counter = 0;
                        if (log_counter < 5)
                        {
                            std::cout << "[x86 GStreamer Hardware Encoder] Sending encoded frame of "
                                      << frame_data.size() << " bytes" << std::endl;
                            ++log_counter;
                        }

                        sender_.sendFragmentedPacket(data, frame_data.size());                
                    }
                }
            }

            std::cout << "[x86 GStreamer Hardware Encoder] Frame retrieval thread stopped" << std::endl;
        }

        inline std::string getEnv(const std::string& key, const std::string& defaultValue = "") {
            const char* val = std::getenv(key.c_str());
            return val ? std::string(val) : defaultValue;
        }
        int getEnvInt(const std::string& key, int defaultValue) {
            std::string value = getEnv(key);
            if (value.empty()) {
                return defaultValue;
            }
            try {
                return std::stoi(value);
            } catch (...) {
                return defaultValue;
            }
        }

        void set_id_timestamp(const int id)
        {
            if (is_shutdown_)
            {
                return;
            }
            try
            {
                if (id < 0)
                {
                    return;
                }

                int frame_id = id;
                int timestamp = 0;

                // auto parse_value = [](const std::string &text) -> int {
                //     return std::stoi(text);
                // };

                // size_t separator = id_timestamp.find_first_of(":");
                // if (separator != std::string::npos)
                // {
                //     std::string first = id_timestamp.substr(0, separator);
                //     std::string second = id_timestamp.substr(separator + 1);
                //     frame_id = parse_value(first);
                //     timestamp = parse_value(second);
                // }
                // else
                // {
                //     frame_id = parse_value(id_timestamp);
                //     timestamp = frame_id;
                // }
                sender_.setReceiveTimestamp(frame_id, timestamp);
            }
            catch (const std::exception &e)
            {
                std::cerr << "[x86 GStreamer Hardware Encoder] Failed to parse id_timestamp '"
                          << id << "': " << e.what() << std::endl;
            }
        }

        bool set_bitrate(int bitrate)
        {
            if (!encoder_->is_initialized())
            {
                std::cerr << "[x86 GStreamer Hardware Encoder] Encoder not initialized!" << std::endl;
                return false;
            }
            bitrate_ = bitrate;
            return encoder_->set_bitrate(bitrate);
        }

        void set_publish_av1_video_data(bool value)
        {
            publish_av1_video_data_ = value;
        }

        bool get_publish_av1_video_data()
        {
            return publish_av1_video_data_;
        }

        void reset_frame_id()
        {
            sender_.setFrameId(0);
        }

        // ⭐ 新增：获取视频RTT (毫秒)
        double get_video_rtt_ms()
        {
            return sender_.getVideoRttMs();
        }

        int clamp_overlap_width(int requested) const
        {
            if (width_ <= 2) {
                return 0;
            }
            int limited = std::clamp(requested, 0, width_ - 2);
            if (limited % 2 != 0) {
                limited = std::max(0, limited - 1);
            }
            return limited;
        }

        void update_output_geometry()
        {
            effective_overlap_ = clamp_overlap_width(overlap_width);
            output_width_ = (width_ - effective_overlap_) * 2 + effective_overlap_;
            if (output_width_ <= 0) {
                output_width_ = width_ * 2;
                effective_overlap_ = 0;
            }
            stitched_frame_.release();
            yuv420_frame_.release();
        }

        size_t fused_yuv_size() const
        {
            return static_cast<size_t>(output_width_) * height_ * 3 / 2;
        }

        bool validate_frame_shape(const py::buffer_info& info, const char* label)
        {
            if (info.ndim != 3) {
                std::cerr << "[x86 GStreamer Hardware Encoder] " << label << " frame must be 3D (HxWxC)" << std::endl;
                return false;
            }
            if (info.shape[0] != height_ || info.shape[1] != width_ || info.shape[2] != 3) {
                if (!reported_shape_warning_) {
                    std::cerr << "[x86 GStreamer Hardware Encoder] " << label << " frame shape mismatch: "
                              << info.shape[1] << "x" << info.shape[0] << " (expected "
                              << width_ << "x" << height_ << ")" << std::endl;
                    reported_shape_warning_ = true;
                }
                return false;
            }
            return true;
        }

        void ensure_frame_buffers()
        {
            if (stitched_frame_.empty() || stitched_frame_.rows != height_ || stitched_frame_.cols != output_width_) {
                stitched_frame_.create(height_, output_width_, CV_8UC3);
            }
            if (yuv420_frame_.empty() || yuv420_frame_.rows != height_ * 3 / 2 || yuv420_frame_.cols != output_width_) {
                yuv420_frame_.create(height_ * 3 / 2, output_width_, CV_8UC1);
            }
        }

        void blend_overlap_region(const cv::Mat& left_overlap,
                                  const cv::Mat& right_overlap,
                                  cv::Mat& blended) const
        {
            if (left_overlap.empty() || right_overlap.empty() || blended.empty()) {
                return;
            }
            const int overlap_w = blended.cols;
            const int height = blended.rows;
            const int max_den = std::max(overlap_w - 1, 1);
            for (int y = 0; y < height; ++y) {
                const cv::Vec3b* l_ptr = left_overlap.ptr<cv::Vec3b>(y);
                const cv::Vec3b* r_ptr = right_overlap.ptr<cv::Vec3b>(y);
                cv::Vec3b* dst_ptr = blended.ptr<cv::Vec3b>(y);
                for (int x = 0; x < overlap_w; ++x) {
                    const float alpha = static_cast<float>(x) / static_cast<float>(max_den);
                    const float beta = 1.0f - alpha;
                    cv::Vec3b pixel;
                    for (int c = 0; c < 3; ++c) {
                        pixel[c] = cv::saturate_cast<uint8_t>(l_ptr[x][c] * beta + r_ptr[x][c] * alpha);
                    }
                    dst_ptr[x] = pixel;
                }
            }
        }

        void maybe_dump_fused_frame(const cv::Mat& fused)
        {
            if (!debug_save_fused_ || fused.empty()) {
                return;
            }
            ++debug_frame_counter_;
            if ((debug_frame_counter_ - 1) % debug_sample_interval_ != 0) {
                return;
            }
            if (debug_saved_frames_ >= debug_max_debug_frames_) {
                return;
            }
            try {
                std::ostringstream oss;
                oss << debug_fused_dir_ << "/fused_" << std::setw(6) << std::setfill('0') << debug_saved_frames_ << ".png";
                cv::imwrite(oss.str(), fused);
                ++debug_saved_frames_;
            } catch (const std::exception &e) {
                std::cerr << "[x86 GStreamer Hardware Encoder] Failed to dump fused frame: " << e.what() << std::endl;
                debug_save_fused_ = false;
            }
        }

        void configure_debug_fused(bool enable,
                                   const std::string& directory,
                                   int sample_interval,
                                   int max_frames)
        {
            debug_sample_interval_ = std::max(1, sample_interval);
            debug_max_debug_frames_ = std::max(1, max_frames);
            debug_saved_frames_ = 0;
            debug_frame_counter_ = 0;

            if (!enable)
            {
                debug_save_fused_ = false;
                debug_fused_dir_.clear();
                std::cout << "[x86 GStreamer Hardware Encoder] Fused-frame debug disabled" << std::endl;
                return;
            }

            try
            {
                fs::path base_dir(directory.empty() ? fs::path("video_debug") / "fused_frames" : fs::path(directory));
                if (!base_dir.is_absolute())
                {
                    base_dir = fs::current_path() / base_dir;
                }

                auto now = std::chrono::system_clock::now();
                std::time_t t = std::chrono::system_clock::to_time_t(now);
                std::tm tm_buf;
                localtime_r(&t, &tm_buf);
                std::ostringstream oss;
                oss << std::put_time(&tm_buf, "%Y%m%d_%H%M%S");
                fs::path session_dir = base_dir / oss.str();

                fs::create_directories(session_dir);
                debug_fused_dir_ = session_dir.string();
                debug_save_fused_ = true;
                std::cout << "[x86 GStreamer Hardware Encoder] Fused-frame debug enabled -> "
                          << debug_fused_dir_ << std::endl;
            }
            catch (const std::exception &e)
            {
                debug_save_fused_ = false;
                debug_fused_dir_.clear();
                std::cerr << "[x86 GStreamer Hardware Encoder] Failed to configure fused debug: "
                          << e.what() << std::endl;
            }
        }

    private:
        std::unique_ptr<GStreamerEncoder> encoder_;
        Sender sender_;
        std::string server_ip_;
        uint16_t port_ = 0;

        int width_;
        int height_;
        int overlap_width;
        int fps_;
        int bitrate_;
        std::string encoder_type_;

        bool is_shutdown_;
        bool publish_av1_video_data_;
        std::thread sending_thread_;

        std::vector<std::vector<uint8_t>> output_buffers_;
        std::mutex output_buffers_mutex_;
        std::ofstream ivf_file_;
        bool save_frame_ = false;
        bool ivf_enabled_ = false;
        uint64_t ivf_frame_index_ = 0;
        std::unique_ptr<RosVideoPublisher> ros_video_pub_;
        int output_width_ = 0;
        int effective_overlap_ = 0;
        cv::Mat stitched_frame_;
        cv::Mat yuv420_frame_;
        bool reported_shape_warning_ = false;
        bool debug_save_fused_ = false;
        int debug_sample_interval_ = 1;
        int debug_saved_frames_ = 0;
        int debug_max_debug_frames_ = 0;
        int debug_frame_counter_ = 0;
        std::string debug_fused_dir_;
    };

} // namespace nv_video_encode

PYBIND11_MODULE(nv_video_encode, m)
{
    m.doc() = "x86 GStreamer Hardware Video Encoder Module (NVENC)";

    py::class_<nv_video_encode::NvVideoEncode>(m, "NvVideoEncode")
        .def(py::init<const std::string &,
                      const std::string &,
                      int,
                      int>(),
             py::arg("server_ip"),
             py::arg("mac_address"),
             py::arg("remote_port"),
             py::arg("local_port") = -1)
        .def("initialise", &nv_video_encode::NvVideoEncode::initialise)
        .def("encode", &nv_video_encode::NvVideoEncode::encode)
        .def("blend_encode", &nv_video_encode::NvVideoEncode::blend_encode)
        .def("get_encoded_frame", &nv_video_encode::NvVideoEncode::get_encoded_frame)
        .def("set_id_timestamp", &nv_video_encode::NvVideoEncode::set_id_timestamp)
        .def("set_bitrate", &nv_video_encode::NvVideoEncode::set_bitrate)
        .def("set_publish_av1_video_data", &nv_video_encode::NvVideoEncode::set_publish_av1_video_data)
        .def("get_publish_av1_video_data", &nv_video_encode::NvVideoEncode::get_publish_av1_video_data)
        .def("reset_frame_id", &nv_video_encode::NvVideoEncode::reset_frame_id)
        .def("get_video_rtt_ms", &nv_video_encode::NvVideoEncode::get_video_rtt_ms)
        .def("configure_debug_fused", &nv_video_encode::NvVideoEncode::configure_debug_fused);
}
