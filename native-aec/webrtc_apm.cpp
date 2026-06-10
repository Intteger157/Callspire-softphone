/*
 * webrtc_apm.cpp — WebRTC Audio Processing Module (AEC3) for Callspire.
 *
 * C-export surface must match NativeAec.cs:
 *   void* apm_create(int sample_rate, int channels, int frame_size_ms);
 *   void  apm_destroy(void* state);
 *   int   apm_process_render(void* state, short* render_data, int num_samples);
 *   int   apm_process_capture(void* state, short* capture_data, int num_samples);
 *
 * The WebRTC APM int16 interface accepts ONLY 10 ms chunks.
 * If frame_size_ms passed from C# is 20 ms (160 samples @ 8 kHz mono),
 * this wrapper splits render/capture buffers into two sequential 10 ms calls.
 */

#include <cstdint>
#include <cstdlib>
#include <algorithm>
#include <mutex>

// WebRTC APM headers.
// The dependency webrtc-audio-processing-cmake provides include directories so
// these headers resolve as in upstream WebRTC.
#include "modules/audio_processing/include/audio_processing.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

namespace
{
    struct ApmContext
    {
        std::mutex mtx;
        webrtc::AudioProcessing* apm = nullptr;

        int sample_rate_hz = 0;
        int channels = 1;
        int frame_size_ms = 0;

        // Cached 10ms stream configs (APM's int16 interface).
        webrtc::StreamConfig input_config;
        webrtc::StreamConfig output_config;
        webrtc::StreamConfig reverse_input_config;
        webrtc::StreamConfig reverse_output_config;

        // int16 interleaved samples per 10ms chunk (includes channels).
        int samples_per_10ms = 0;

        // Expected samples per incoming (C#) frame_size_ms.
        int samples_per_frame = 0;
    };

    // Helper: split a buffer into strict 10ms chunks and run a callback on each chunk.
    template <typename Fn>
    static int ForEach10msChunk(ApmContext* ctx, short* data, int num_samples, Fn&& fn)
    {
        if (!ctx || !ctx->apm || !data || num_samples <= 0) return -1;
        if (ctx->samples_per_10ms <= 0) return -2;
        if (ctx->samples_per_frame <= 0) return -3;

        // Strictly enforce the "incoming frame" size (e.g. 20ms -> 2x10ms).
        if (num_samples != ctx->samples_per_frame) return -4;

        if (num_samples % ctx->samples_per_10ms != 0)
        {
            // We require strict 10ms chunks; return an error without modifying data.
            return -5;
        }

        const int16_t* const src_i16 = reinterpret_cast<const int16_t*>(data);
        int16_t* const dest_i16 = reinterpret_cast<int16_t*>(data);

        const int total_chunks = num_samples / ctx->samples_per_10ms;
        for (int chunk = 0; chunk < total_chunks; ++chunk)
        {
            const int offset_samples = chunk * ctx->samples_per_10ms;
            const int16_t* const chunk_src = src_i16 + offset_samples;
            int16_t* const chunk_dest = dest_i16 + offset_samples;
            int ret = fn(chunk_src, chunk_dest);
            if (ret != 0) return ret;
        }
        return 0;
    }
} // namespace

extern "C"
{
    EXPORT void* apm_create(int sample_rate, int channels, int frame_size_ms)
    {
        if (sample_rate <= 0 || channels <= 0 || frame_size_ms <= 0) return nullptr;
        if (sample_rate != 8000 && sample_rate != 16000 && sample_rate != 32000 && sample_rate != 48000)
            return nullptr;

        // WebRTC expects 10ms chunks: samples_per_10ms = sample_rate / 100.
        const int samples_per_10ms_per_channel = sample_rate / 100;
        const int samples_per_10ms_total = samples_per_10ms_per_channel * channels;
        if (frame_size_ms % 10 != 0) return nullptr;

        auto* ctx = new ApmContext();
        ctx->sample_rate_hz = sample_rate;
        ctx->channels = channels;
        ctx->frame_size_ms = frame_size_ms;
        ctx->samples_per_10ms = samples_per_10ms_total;
        const int frames_per_input = frame_size_ms / 10;
        ctx->samples_per_frame = samples_per_10ms_total * frames_per_input;

        // Stream configs define chunk size: AudioProcessing::kChunkSizeMs == 10.
        // So these configs already match exactly the APM required frame length.
        ctx->input_config = webrtc::StreamConfig(sample_rate, static_cast<size_t>(channels), false);
        ctx->output_config = webrtc::StreamConfig(sample_rate, static_cast<size_t>(channels), false);
        ctx->reverse_input_config = webrtc::StreamConfig(sample_rate, static_cast<size_t>(channels), false);
        ctx->reverse_output_config = webrtc::StreamConfig(sample_rate, static_cast<size_t>(channels), false);

        // Configure APM components: AEC3 + HPF + NS + AGC.
        webrtc::AudioProcessing::Config cfg;
        cfg.echo_canceller.enabled = true; // AEC3
        cfg.high_pass_filter.enabled = true;

        // NS: High/Very High as requested.
        cfg.noise_suppression.enabled = true;
        cfg.noise_suppression.level = webrtc::AudioProcessing::Config::NoiseSuppression::Level::kVeryHigh;

        // AGC: enable AGC1 in adaptive digital mode (no need for analog level callbacks).
        cfg.gain_controller1.enabled = true;
        cfg.gain_controller1.mode =
            webrtc::AudioProcessing::Config::GainController1::Mode::kAdaptiveDigital;

        // Build APM instance via AudioProcessingBuilder (as required).
        webrtc::AudioProcessingBuilder builder;
        ctx->apm = builder.Create();
        if (!ctx->apm)
        {
            delete ctx;
            return nullptr;
        }

        ctx->apm->ApplyConfig(cfg);

        // Initialize the module with our stream formats.
        webrtc::ProcessingConfig processing_config;
        processing_config.input_stream() = ctx->input_config;
        processing_config.output_stream() = ctx->output_config;
        processing_config.reverse_input_stream() = ctx->reverse_input_config;
        processing_config.reverse_output_stream() = ctx->reverse_output_config;

        const int init_ret = ctx->apm->Initialize(processing_config);
        if (init_ret != 0)
        {
            delete ctx->apm;
            ctx->apm = nullptr;
            delete ctx;
            return nullptr;
        }

        // set_stream_delay_ms() is required by the interface when echo processing is enabled.
        // We don't have perfect pipeline timing here, but this value is a reasonable
        // starting point for the current Callspire WASAPI buffering (~20ms frames).
        //
        // If echo artifacts persist, tune this value (e.g. 150..400 ms).
        const int delay_ms = std::max(0, frame_size_ms * 12); // 20ms -> 240ms
        ctx->apm->set_stream_delay_ms(delay_ms);

        return ctx;
    }

    EXPORT void apm_destroy(void* state)
    {
        auto* ctx = static_cast<ApmContext*>(state);
        if (!ctx) return;
        // APM uses ref-count interface; builder examples use delete.
        if (ctx->apm) delete ctx->apm;
        delete ctx;
    }

    EXPORT int apm_process_render(void* state, short* render_data, int num_samples)
    {
        auto* ctx = static_cast<ApmContext*>(state);
        if (!ctx || !ctx->apm || !render_data || num_samples <= 0) return -1;

        std::lock_guard<std::mutex> _l(ctx->mtx);

        // Feed far-end (render) stream to APM as the reverse direction.
        return ForEach10msChunk(ctx, render_data, num_samples,
            [&](const int16_t* chunk_src, int16_t* chunk_dest) -> int
            {
                // src/dest can be the same buffer.
                const int ret = ctx->apm->ProcessReverseStream(
                    chunk_src, ctx->reverse_input_config, ctx->reverse_output_config, chunk_dest);
                return ret;
            });
    }

    EXPORT int apm_process_capture(void* state, short* capture_data, int num_samples)
    {
        auto* ctx = static_cast<ApmContext*>(state);
        if (!ctx || !ctx->apm || !capture_data || num_samples <= 0) return -1;

        std::lock_guard<std::mutex> _l(ctx->mtx);

        // Process near-end (capture) stream through AEC3.
        return ForEach10msChunk(ctx, capture_data, num_samples,
            [&](const int16_t* chunk_src, int16_t* chunk_dest) -> int
            {
                const int ret = ctx->apm->ProcessStream(
                    chunk_src, ctx->input_config, ctx->output_config, chunk_dest);
                return ret;
            });
    }
}

