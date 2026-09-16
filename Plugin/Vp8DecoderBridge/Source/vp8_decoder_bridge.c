#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef int vpx_codec_err_t;
typedef long vpx_codec_flags_t;
typedef const void *vpx_codec_iter_t;

typedef struct vpx_codec_iface vpx_codec_iface_t;
typedef struct vpx_codec_priv vpx_codec_priv_t;

typedef struct vpx_codec_dec_cfg
{
    unsigned int threads;
    unsigned int w;
    unsigned int h;
} vpx_codec_dec_cfg_t;

typedef struct vpx_codec_ctx
{
    const char *name;
    vpx_codec_iface_t *iface;
    vpx_codec_err_t err;
    const char *err_detail;
    vpx_codec_flags_t init_flags;
    union
    {
        const vpx_codec_dec_cfg_t *dec;
        const void *raw;
    } config;
    vpx_codec_priv_t *priv;
} vpx_codec_ctx_t;

typedef enum vpx_img_fmt
{
    VPX_IMG_FMT_NONE = 0,
    VPX_IMG_FMT_PLANAR = 0x100,
    VPX_IMG_FMT_UV_FLIP = 0x200,
    VPX_IMG_FMT_HAS_ALPHA = 0x400,
    VPX_IMG_FMT_HIGHBITDEPTH = 0x800,
    VPX_IMG_FMT_I420 = VPX_IMG_FMT_PLANAR | 2
} vpx_img_fmt_t;

typedef struct vpx_image
{
    vpx_img_fmt_t fmt;
    int cs;
    int range;
    unsigned int w;
    unsigned int h;
    unsigned int bit_depth;
    unsigned int d_w;
    unsigned int d_h;
    unsigned int r_w;
    unsigned int r_h;
    unsigned int x_chroma_shift;
    unsigned int y_chroma_shift;
    unsigned char *planes[4];
    int stride[4];
    int bps;
    void *user_priv;
    unsigned char *img_data;
    int img_data_owner;
    int self_allocd;
} vpx_image_t;

typedef struct vpx_codec_stream_info
{
    unsigned int sz;
    unsigned int w;
    unsigned int h;
    unsigned int is_kf;
} vpx_codec_stream_info_t;

typedef vpx_codec_iface_t *(*vpx_codec_vp8_dx_fn)(void);
typedef vpx_codec_err_t (*vpx_codec_dec_init_ver_fn)(vpx_codec_ctx_t *ctx,
                                                     vpx_codec_iface_t *iface,
                                                     const vpx_codec_dec_cfg_t *cfg,
                                                     vpx_codec_flags_t flags,
                                                     int ver);
typedef vpx_codec_err_t (*vpx_codec_destroy_fn)(vpx_codec_ctx_t *ctx);
typedef vpx_codec_err_t (*vpx_codec_decode_fn)(vpx_codec_ctx_t *ctx,
                                               const uint8_t *data,
                                               unsigned int data_sz,
                                               void *user_priv,
                                               long deadline);
typedef vpx_image_t *(*vpx_codec_get_frame_fn)(vpx_codec_ctx_t *ctx, vpx_codec_iter_t *iter);
typedef vpx_codec_err_t (*vpx_codec_peek_stream_info_fn)(vpx_codec_iface_t *iface,
                                                         const uint8_t *data,
                                                         unsigned int data_sz,
                                                         vpx_codec_stream_info_t *si);
typedef const char *(*vpx_codec_error_fn)(vpx_codec_ctx_t *ctx);
typedef const char *(*vpx_codec_error_detail_fn)(vpx_codec_ctx_t *ctx);

typedef struct vp8_decoder_bridge
{
    void *lib_handle;
    vpx_codec_vp8_dx_fn codec_vp8_dx;
    vpx_codec_dec_init_ver_fn codec_dec_init_ver;
    vpx_codec_destroy_fn codec_destroy;
    vpx_codec_decode_fn codec_decode;
    vpx_codec_get_frame_fn codec_get_frame;
    vpx_codec_peek_stream_info_fn codec_peek_stream_info;
    vpx_codec_error_fn codec_error;
    vpx_codec_error_detail_fn codec_error_detail;
    vpx_codec_ctx_t codec_ctx;
    int abi_version;
    int is_initialized;
    unsigned int last_width;
    unsigned int last_height;
    int last_pixel_format;
    unsigned char *last_y_plane;
    unsigned char *last_u_plane;
    unsigned char *last_v_plane;
    int last_y_stride;
    int last_u_stride;
    int last_v_stride;
    char last_error[256];
    char last_debug[1024];
} vp8_decoder_bridge_t;

static void bridge_set_error(vp8_decoder_bridge_t *bridge, const char *message)
{
    if (bridge == NULL)
    {
        return;
    }

    if (message == NULL)
    {
        bridge->last_error[0] = '\0';
        return;
    }

    snprintf(bridge->last_error, sizeof(bridge->last_error), "%s", message);
}

static void bridge_set_debug(vp8_decoder_bridge_t *bridge, const char *message)
{
    if (bridge == NULL)
    {
        return;
    }

    if (message == NULL)
    {
        bridge->last_debug[0] = '\0';
        return;
    }

    snprintf(bridge->last_debug, sizeof(bridge->last_debug), "%s", message);
}

static void bridge_set_codec_error(vp8_decoder_bridge_t *bridge, const char *prefix)
{
    const char *error = NULL;
    const char *detail = NULL;

    if (bridge == NULL)
    {
        return;
    }

    if (bridge->codec_error != NULL)
    {
        error = bridge->codec_error(&bridge->codec_ctx);
    }

    if (bridge->codec_error_detail != NULL)
    {
        detail = bridge->codec_error_detail(&bridge->codec_ctx);
    }

    if (detail != NULL && detail[0] != '\0')
    {
        snprintf(bridge->last_error, sizeof(bridge->last_error), "%s: %s (%s)", prefix, error != NULL ? error : "<unknown>", detail);
    }
    else
    {
        snprintf(bridge->last_error, sizeof(bridge->last_error), "%s: %s", prefix, error != NULL ? error : "<unknown>");
    }
}

static const char *bridge_get_pixel_format_name(vpx_img_fmt_t fmt)
{
    if (fmt == VPX_IMG_FMT_I420)
    {
        return "I420";
    }

    if ((fmt & VPX_IMG_FMT_PLANAR) == VPX_IMG_FMT_PLANAR)
    {
        return "PLANAR";
    }

    return "UNKNOWN";
}

static uint8_t bridge_clamp_to_byte(int value)
{
    if (value < 0)
    {
        return 0;
    }

    if (value > 255)
    {
        return 255;
    }

    return (uint8_t)value;
}

static void bridge_store_last_frame(vp8_decoder_bridge_t *bridge, vpx_image_t *image)
{
    if (bridge == NULL || image == NULL)
    {
        return;
    }

    bridge->last_width = image->d_w;
    bridge->last_height = image->d_h;
    bridge->last_pixel_format = (int)image->fmt;
    bridge->last_y_plane = image->planes[0];
    bridge->last_u_plane = image->planes[1];
    bridge->last_v_plane = image->planes[2];
    bridge->last_y_stride = image->stride[0];
    bridge->last_u_stride = image->stride[1];
    bridge->last_v_stride = image->stride[2];
}

static int bridge_try_initialize(vp8_decoder_bridge_t *bridge)
{
    int abi_version;

    if (bridge == NULL)
    {
        return 0;
    }

    memset(&bridge->codec_ctx, 0, sizeof(bridge->codec_ctx));

    for (abi_version = 1; abi_version <= 64; ++abi_version)
    {
        vpx_codec_iface_t *iface = bridge->codec_vp8_dx();
        vpx_codec_err_t result = bridge->codec_dec_init_ver(&bridge->codec_ctx, iface, NULL, 0, abi_version);
        if (result == 0)
        {
            bridge->abi_version = abi_version;
            bridge->is_initialized = 1;
            bridge_set_error(bridge, NULL);
            return 1;
        }

        memset(&bridge->codec_ctx, 0, sizeof(bridge->codec_ctx));
    }

    bridge_set_error(bridge, "Failed to initialize libvpx decoder for any tested ABI version.");
    return 0;
}

__attribute__((visibility("default"))) void *vp8_decoder_create(void)
{
    const char *library_names[] = { "libvpx.so.9", "libvpx.so", NULL };
    const char **library_name = library_names;
    vp8_decoder_bridge_t *bridge = (vp8_decoder_bridge_t *)calloc(1, sizeof(vp8_decoder_bridge_t));
    if (bridge == NULL)
    {
        return NULL;
    }

    while (*library_name != NULL && bridge->lib_handle == NULL)
    {
        bridge->lib_handle = dlopen(*library_name, RTLD_NOW | RTLD_LOCAL);
        library_name++;
    }

    if (bridge->lib_handle == NULL)
    {
        bridge_set_error(bridge, "Failed to dlopen libvpx.so.9.");
        return bridge;
    }

    bridge->codec_vp8_dx = (vpx_codec_vp8_dx_fn)dlsym(bridge->lib_handle, "vpx_codec_vp8_dx");
    bridge->codec_dec_init_ver = (vpx_codec_dec_init_ver_fn)dlsym(bridge->lib_handle, "vpx_codec_dec_init_ver");
    bridge->codec_destroy = (vpx_codec_destroy_fn)dlsym(bridge->lib_handle, "vpx_codec_destroy");
    bridge->codec_decode = (vpx_codec_decode_fn)dlsym(bridge->lib_handle, "vpx_codec_decode");
    bridge->codec_get_frame = (vpx_codec_get_frame_fn)dlsym(bridge->lib_handle, "vpx_codec_get_frame");
    bridge->codec_peek_stream_info = (vpx_codec_peek_stream_info_fn)dlsym(bridge->lib_handle, "vpx_codec_peek_stream_info");
    bridge->codec_error = (vpx_codec_error_fn)dlsym(bridge->lib_handle, "vpx_codec_error");
    bridge->codec_error_detail = (vpx_codec_error_detail_fn)dlsym(bridge->lib_handle, "vpx_codec_error_detail");

    if (bridge->codec_vp8_dx == NULL ||
        bridge->codec_dec_init_ver == NULL ||
        bridge->codec_destroy == NULL ||
        bridge->codec_decode == NULL ||
        bridge->codec_get_frame == NULL ||
        bridge->codec_peek_stream_info == NULL)
    {
        bridge_set_error(bridge, "Failed to resolve required libvpx symbols.");
        return bridge;
    }

    bridge_try_initialize(bridge);
    return bridge;
}

__attribute__((visibility("default"))) void vp8_decoder_destroy(void *decoder)
{
    vp8_decoder_bridge_t *bridge = (vp8_decoder_bridge_t *)decoder;
    if (bridge == NULL)
    {
        return;
    }

    if (bridge->is_initialized && bridge->codec_destroy != NULL)
    {
        bridge->codec_destroy(&bridge->codec_ctx);
    }

    if (bridge->lib_handle != NULL)
    {
        dlclose(bridge->lib_handle);
    }

    free(bridge);
}

__attribute__((visibility("default"))) int vp8_decoder_decode(void *decoder,
                                                             const uint8_t *data,
                                                             int data_length,
                                                             int *out_width,
                                                             int *out_height,
                                                             int *out_pixel_format,
                                                             intptr_t *out_y_plane,
                                                             intptr_t *out_u_plane,
                                                             intptr_t *out_v_plane,
                                                             int *out_y_stride,
                                                             int *out_u_stride,
                                                             int *out_v_stride,
                                                             char *out_format_name,
                                                             int out_format_name_capacity)
{
    vp8_decoder_bridge_t *bridge = (vp8_decoder_bridge_t *)decoder;
    vpx_codec_stream_info_t stream_info;
    vpx_codec_iter_t iter = NULL;
    vpx_image_t *image = NULL;
    vpx_codec_err_t decode_result;
    int frame_count = 0;
    char input_bytes[16 * 3 + 1];
    int input_offset = 0;
    const char *codec_error = NULL;
    const char *codec_error_detail = NULL;

    bridge_set_debug(bridge, NULL);

    if (out_width != NULL)
    {
        *out_width = 0;
    }

    if (out_height != NULL)
    {
        *out_height = 0;
    }

    if (out_pixel_format != NULL)
    {
        *out_pixel_format = 0;
    }

    if (out_y_plane != NULL)
    {
        *out_y_plane = 0;
    }

    if (out_u_plane != NULL)
    {
        *out_u_plane = 0;
    }

    if (out_v_plane != NULL)
    {
        *out_v_plane = 0;
    }

    if (out_y_stride != NULL)
    {
        *out_y_stride = 0;
    }

    if (out_u_stride != NULL)
    {
        *out_u_stride = 0;
    }

    if (out_v_stride != NULL)
    {
        *out_v_stride = 0;
    }

    if (out_format_name != NULL && out_format_name_capacity > 0)
    {
        out_format_name[0] = '\0';
    }

    if (bridge == NULL || data == NULL || data_length <= 0)
    {
        return 0;
    }

    memset(input_bytes, 0, sizeof(input_bytes));
    for (int i = 0; i < data_length && i < 16 && input_offset < (int)sizeof(input_bytes) - 1; ++i)
    {
        input_offset += snprintf(input_bytes + input_offset, sizeof(input_bytes) - (size_t)input_offset, "%02X", data[i]);
        if (i < 15 && i < data_length - 1 && input_offset < (int)sizeof(input_bytes) - 1)
        {
            input_bytes[input_offset++] = ' ';
            input_bytes[input_offset] = '\0';
        }
    }

    if (!bridge->is_initialized)
    {
        bridge_set_error(bridge, "Decoder is not initialized.");
        bridge_set_debug(bridge, "Decoder is not initialized.");
        return 0;
    }

    memset(&stream_info, 0, sizeof(stream_info));
    stream_info.sz = sizeof(stream_info);
    bridge->codec_peek_stream_info(bridge->codec_vp8_dx(), data, (unsigned int)data_length, &stream_info);

    decode_result = bridge->codec_decode(&bridge->codec_ctx, data, (unsigned int)data_length, NULL, 0);
    codec_error = bridge->codec_error != NULL ? bridge->codec_error(&bridge->codec_ctx) : NULL;
    codec_error_detail = bridge->codec_error_detail != NULL ? bridge->codec_error_detail(&bridge->codec_ctx) : NULL;

    if (decode_result != 0)
    {
        bridge_set_codec_error(bridge, "vpx_codec_decode failed");
        snprintf(
            bridge->last_debug,
            sizeof(bridge->last_debug),
            "decode_result=%d error=%s error_detail=%s first16=%s frame_count=0",
            decode_result,
            codec_error != NULL ? codec_error : "<null>",
            (codec_error_detail != NULL && codec_error_detail[0] != '\0') ? codec_error_detail : "<none>",
            input_bytes);
        return 0;
    }

    while ((image = bridge->codec_get_frame(&bridge->codec_ctx, &iter)) != NULL)
    {
        frame_count++;
        if (frame_count == 1)
        {
            break;
        }
    }

    if (image == NULL)
    {
        bridge_set_error(bridge, "Decode succeeded but no output image was returned.");
        snprintf(
            bridge->last_debug,
            sizeof(bridge->last_debug),
            "decode_result=%d error=%s error_detail=%s first16=%s frame_count=%d No decoded frame returned",
            decode_result,
            codec_error != NULL ? codec_error : "<null>",
            (codec_error_detail != NULL && codec_error_detail[0] != '\0') ? codec_error_detail : "<none>",
            input_bytes,
            frame_count);
        return 0;
    }

    if (out_width != NULL)
    {
        *out_width = (int)(image->d_w != 0 ? image->d_w : stream_info.w);
    }

    if (out_height != NULL)
    {
        *out_height = (int)(image->d_h != 0 ? image->d_h : stream_info.h);
    }

    if (out_pixel_format != NULL)
    {
        *out_pixel_format = (int)image->fmt;
    }

    if (out_y_plane != NULL)
    {
        *out_y_plane = (intptr_t)image->planes[0];
    }

    if (out_u_plane != NULL)
    {
        *out_u_plane = (intptr_t)image->planes[1];
    }

    if (out_v_plane != NULL)
    {
        *out_v_plane = (intptr_t)image->planes[2];
    }

    if (out_y_stride != NULL)
    {
        *out_y_stride = image->stride[0];
    }

    if (out_u_stride != NULL)
    {
        *out_u_stride = image->stride[1];
    }

    if (out_v_stride != NULL)
    {
        *out_v_stride = image->stride[2];
    }

    if (out_format_name != NULL && out_format_name_capacity > 0)
    {
        snprintf(out_format_name, (size_t)out_format_name_capacity, "%s", bridge_get_pixel_format_name(image->fmt));
    }

    bridge_set_error(bridge, NULL);
    bridge_store_last_frame(bridge, image);
    snprintf(
        bridge->last_debug,
        sizeof(bridge->last_debug),
        "decode_result=%d error=%s error_detail=%s first16=%s frame_count=%d image_fmt=%d image_w=%u image_h=%u image_d_w=%u image_d_h=%u",
        decode_result,
        codec_error != NULL ? codec_error : "<null>",
        (codec_error_detail != NULL && codec_error_detail[0] != '\0') ? codec_error_detail : "<none>",
        input_bytes,
        frame_count,
        (int)image->fmt,
        image->w,
        image->h,
        image->d_w,
        image->d_h);
    return 1;
}

__attribute__((visibility("default"))) int vp8_decoder_get_width(void *decoder)
{
    vp8_decoder_bridge_t *bridge = (vp8_decoder_bridge_t *)decoder;
    return bridge != NULL ? (int)bridge->last_width : 0;
}

__attribute__((visibility("default"))) int vp8_decoder_get_height(void *decoder)
{
    vp8_decoder_bridge_t *bridge = (vp8_decoder_bridge_t *)decoder;
    return bridge != NULL ? (int)bridge->last_height : 0;
}

__attribute__((visibility("default"))) int vp8_decoder_copy_rgba(void *decoder, uint8_t *dst, int dst_size)
{
    vp8_decoder_bridge_t *bridge = (vp8_decoder_bridge_t *)decoder;
    int width;
    int height;
    int required_size;

    if (bridge == NULL || dst == NULL)
    {
        return 0;
    }

    width = (int)bridge->last_width;
    height = (int)bridge->last_height;
    required_size = width * height * 4;

    if (width <= 0 || height <= 0)
    {
        bridge_set_error(bridge, "No decoded frame available for RGBA copy.");
        return 0;
    }

    if (bridge->last_y_plane == NULL || bridge->last_u_plane == NULL || bridge->last_v_plane == NULL)
    {
        bridge_set_error(bridge, "Decoded frame planes are null.");
        return 0;
    }

    if (dst_size < required_size)
    {
        bridge_set_error(bridge, "Destination RGBA buffer is too small.");
        return 0;
    }

    for (int y = 0; y < height; ++y)
    {
        const unsigned char *y_row = bridge->last_y_plane + (y * bridge->last_y_stride);
        const unsigned char *u_row = bridge->last_u_plane + ((y >> 1) * bridge->last_u_stride);
        const unsigned char *v_row = bridge->last_v_plane + ((y >> 1) * bridge->last_v_stride);
        uint8_t *dst_row = dst + (y * width * 4);

        for (int x = 0; x < width; ++x)
        {
            int y_value = (int)y_row[x];
            int u_value = (int)u_row[x >> 1] - 128;
            int v_value = (int)v_row[x >> 1] - 128;

            int c = y_value - 16;
            int d = u_value;
            int e = v_value;

            int r = (298 * c + 409 * e + 128) >> 8;
            int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
            int b = (298 * c + 516 * d + 128) >> 8;

            int dst_index = x * 4;
            dst_row[dst_index + 0] = bridge_clamp_to_byte(r);
            dst_row[dst_index + 1] = bridge_clamp_to_byte(g);
            dst_row[dst_index + 2] = bridge_clamp_to_byte(b);
            dst_row[dst_index + 3] = 255;
        }
    }

    bridge_set_error(bridge, NULL);
    return 1;
}

__attribute__((visibility("default"))) const char *vp8_decoder_get_last_error(void *decoder)
{
    vp8_decoder_bridge_t *bridge = (vp8_decoder_bridge_t *)decoder;
    if (bridge == NULL)
    {
        return "Decoder handle is null.";
    }

    return bridge->last_error;
}

__attribute__((visibility("default"))) const char *vp8_decoder_get_last_debug(void *decoder)
{
    vp8_decoder_bridge_t *bridge = (vp8_decoder_bridge_t *)decoder;
    if (bridge == NULL)
    {
        return "Decoder handle is null.";
    }

    return bridge->last_debug;
}
