/* Test-only one-translation-unit adapter for deterministic schema-open oracles. */
#if defined(_WIN32) && !defined(_CRT_SECURE_NO_WARNINGS)
#define _CRT_SECURE_NO_WARNINGS
#endif
#include "../src/signal_candy_runtime.c"

#include <errno.h>
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define HARNESS_IMAGE_LIMIT ((size_t)1048576u)
#define CORPUS_MAGIC_SIZE ((size_t)8u)

static const char *status_family(sc_status_t status)
{
    switch (status) {
    case SC_OK: return "ok";
    case SC_ERR_MAGIC: return "magic";
    case SC_ERR_VERSION: return "version";
    case SC_ERR_FEATURE: return "feature";
    case SC_ERR_SIZE: return "size";
    case SC_ERR_BOUNDS: return "bounds";
    case SC_ERR_ALIGN: return "align";
    case SC_ERR_TABLE: return "table";
    case SC_ERR_CRC: return "crc";
    case SC_ERR_LIMIT: return "limit";
    default: return "other";
    }
}

static void print_json_string(const char *text)
{
    const unsigned char *cursor = (const unsigned char *)text;
    putchar('"');
    while (*cursor != 0u) {
        unsigned char value = *cursor++;
        switch (value) {
        case '"': fputs("\\\"", stdout); break;
        case '\\': fputs("\\\\", stdout); break;
        case '\b': fputs("\\b", stdout); break;
        case '\f': fputs("\\f", stdout); break;
        case '\n': fputs("\\n", stdout); break;
        case '\r': fputs("\\r", stdout); break;
        case '\t': fputs("\\t", stdout); break;
        default:
            if (value < 0x20u || value >= 0x7Fu) {
                printf("\\u%04x", (unsigned)value);
            } else {
                putchar((int)value);
            }
            break;
        }
    }
    putchar('"');
}

static void print_null_fields(void)
{
    fputs(",\"featureFlags\":null,\"requiredFeatures\":null"
          ",\"rxMessages\":null,\"rxPrograms\":null"
          ",\"poolSlots\":null,\"conversions\":null"
          ",\"txMessages\":null,\"txPrograms\":null"
          ",\"txCounters\":null,\"rxCounters\":null"
          ",\"coverageSpans\":null,\"nestedMuxRecords\":null"
          ",\"stateBytes\":null,\"scratchBytes\":null", stdout);
}

static int emit_result(const char *id, const uint8_t *image, size_t image_size)
{
    struct sc_schema schema;
    unsigned char canary[sizeof(schema)];
    sc_status_t status;

    memset(&schema, 0xA5, sizeof(schema));
    memcpy(canary, &schema, sizeof(schema));
    status = sc_schema_open(&schema, image, image_size);

    if (status != SC_OK && memcmp(canary, &schema, sizeof(schema)) != 0) {
        fprintf(stderr, "schema canary changed after rejection: %s\n", id);
        return 0;
    }

    fputs("{\"id\":", stdout);
    print_json_string(id);
    printf(",\"status\":%d,\"family\":", (int)status);
    print_json_string(status_family(status));
    printf(",\"accepted\":%s", status == SC_OK ? "true" : "false");

    if (status == SC_OK) {
        printf(",\"featureFlags\":%u,\"requiredFeatures\":%" PRIu32
               ",\"rxMessages\":%u,\"rxPrograms\":%u"
               ",\"poolSlots\":%u,\"conversions\":%u"
               ",\"txMessages\":%u,\"txPrograms\":%u"
               ",\"txCounters\":%u,\"rxCounters\":%u"
               ",\"coverageSpans\":%u,\"nestedMuxRecords\":%u"
               ",\"stateBytes\":%zu,\"scratchBytes\":%zu",
               (unsigned)sc_read_u16(image + 10u), sc_required_features(&schema),
               (unsigned)schema.message_count, (unsigned)schema.signal_count,
               (unsigned)schema.pool_slot_count, (unsigned)schema.conversion_count,
               (unsigned)schema.tx_message_count, (unsigned)schema.tx_program_count,
               (unsigned)schema.counter_count, (unsigned)schema.rx_counter_count,
               (unsigned)schema.span_count, (unsigned)schema.nested_count,
               sc_schema_required_state_bytes(&schema),
               sc_schema_required_scratch_bytes(&schema));
    } else {
        print_null_fields();
    }
    fputs("}\n", stdout);
    return ferror(stdout) == 0;
}

static int read_exact(FILE *file, void *buffer, size_t size)
{
    return size == 0u || fread(buffer, 1u, size, file) == size;
}

static int read_u16_file(FILE *file, uint16_t *value)
{
    uint8_t bytes[2];
    if (!read_exact(file, bytes, sizeof(bytes))) return 0;
    *value = sc_read_u16(bytes);
    return 1;
}

static int read_u32_file(FILE *file, uint32_t *value)
{
    uint8_t bytes[4];
    if (!read_exact(file, bytes, sizeof(bytes))) return 0;
    *value = sc_read_u32(bytes);
    return 1;
}

static int run_pack(const char *path)
{
    static const uint8_t expected_magic[CORPUS_MAGIC_SIZE] = {
        'S', 'C', 'O', 'R', 'P', '0', '1', 0
    };
    uint8_t magic[CORPUS_MAGIC_SIZE];
    uint32_t count;
    uint32_t index;
    FILE *file = fopen(path, "rb");
    int success = 1;

    if (file == NULL) {
        fprintf(stderr, "could not open corpus pack %s: %s\n", path, strerror(errno));
        return 0;
    }
    if (!read_exact(file, magic, sizeof(magic)) ||
        memcmp(magic, expected_magic, sizeof(magic)) != 0 ||
        !read_u32_file(file, &count)) {
        fprintf(stderr, "malformed corpus pack header: %s\n", path);
        fclose(file);
        return 0;
    }

    for (index = 0u; index < count && success; ++index) {
        uint16_t id_size;
        uint32_t image_size;
        char *id;
        uint8_t *image;

        if (!read_u16_file(file, &id_size) || id_size == 0u) {
            success = 0;
            break;
        }
        id = (char *)malloc((size_t)id_size + 1u);
        if (id == NULL || !read_exact(file, id, id_size)) {
            free(id);
            success = 0;
            break;
        }
        id[id_size] = '\0';
        if (!read_u32_file(file, &image_size) || image_size > HARNESS_IMAGE_LIMIT) {
            free(id);
            success = 0;
            break;
        }
        image = (uint8_t *)malloc(image_size == 0u ? 1u : (size_t)image_size);
        if (image == NULL || !read_exact(file, image, image_size)) {
            free(image);
            free(id);
            success = 0;
            break;
        }
        success = emit_result(id, image, image_size);
        free(image);
        free(id);
    }

    if (success && fgetc(file) != EOF) success = 0;
    if (fclose(file) != 0) success = 0;
    if (!success) fprintf(stderr, "malformed or incomplete corpus pack: %s\n", path);
    return success;
}

static int load_image(const char *path, uint8_t **image, size_t *image_size)
{
    FILE *file = fopen(path, "rb");
    long size;
    uint8_t *buffer;

    if (file == NULL || fseek(file, 0L, SEEK_END) != 0 ||
        (size = ftell(file)) < 0L || (size_t)size > HARNESS_IMAGE_LIMIT ||
        fseek(file, 0L, SEEK_SET) != 0) {
        if (file != NULL) fclose(file);
        return 0;
    }
    buffer = (uint8_t *)malloc(size == 0L ? 1u : (size_t)size);
    if (buffer == NULL || !read_exact(file, buffer, (size_t)size) ||
        fclose(file) != 0) {
        free(buffer);
        return 0;
    }
    *image = buffer;
    *image_size = (size_t)size;
    return 1;
}

int main(int argc, char **argv)
{
    if (argc == 3 && strcmp(argv[1], "--pack") == 0) {
        return run_pack(argv[2]) ? EXIT_SUCCESS : EXIT_FAILURE;
    }
    if (argc == 4 && strcmp(argv[1], "--image") == 0) {
        uint8_t *image;
        size_t image_size;
        int success;
        if (!load_image(argv[3], &image, &image_size)) {
            fprintf(stderr, "could not read image: %s\n", argv[3]);
            return EXIT_FAILURE;
        }
        success = emit_result(argv[2], image, image_size);
        free(image);
        return success ? EXIT_SUCCESS : EXIT_FAILURE;
    }
    fprintf(stderr, "usage: schema_open_harness --pack corpus.scorp\n"
                    "       schema_open_harness --image case-id image.scimg\n");
    return 2;
}
