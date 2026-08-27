/*
 * Signal-CANdy differential-vector harness.
 *
 * Format (ASCII, blank lines and lines beginning with # are ignored):
 *   F <bus_agnostic_canid_decimal> <ext0_or_1> <len> <hex_byte>...
 *   E <slot_index> <expected_u64_decimal> <expected_flags_decimal>
 *
 * Each F line starts a fresh zeroed pool, decodes one frame immediately, and
 * is followed by one or more E lines. Hex bytes are exactly two hex digits and
 * their count must equal len. Each E line checks that frame's resulting slot.
 */
#include "signal_candy_runtime.h"

#include <errno.h>
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define IMAGE_CAPACITY ((size_t)1048576u)
#define SLOT_CAPACITY ((size_t)8192u)
#define LINE_CAPACITY 1024u

typedef union {
    void *pointer_alignment;
    uint64_t integer_alignment;
    double double_alignment;
    unsigned char bytes[128];
} schema_storage_t;

static unsigned char image[IMAGE_CAPACITY];
static sc_slot_t pool[SLOT_CAPACITY];

static char *next_token(char **cursor)
{
    char *token = strtok(*cursor, " \t\r\n");
    *cursor = NULL;
    return token;
}

static int parse_u64(const char *text, int base, uint64_t *value)
{
    char *end;
    unsigned long long parsed;

    if (text == NULL || text[0] == '\0' || text[0] == '-') {
        return 0;
    }
    errno = 0;
    parsed = strtoull(text, &end, base);
    if (errno != 0 || *end != '\0') {
        return 0;
    }
    *value = (uint64_t)parsed;
    return 1;
}

static int load_image(const char *path, size_t *size)
{
    FILE *file = fopen(path, "rb");
    long length;

    if (file == NULL) {
        return 0;
    }
    if (fseek(file, 0L, SEEK_END) != 0) {
        fclose(file);
        return 0;
    }
    length = ftell(file);
    if (length < 0L || (size_t)length > IMAGE_CAPACITY ||
        fseek(file, 0L, SEEK_SET) != 0) {
        fclose(file);
        return 0;
    }
    if (fread(image, 1u, (size_t)length, file) != (size_t)length) {
        fclose(file);
        return 0;
    }
    if (fclose(file) != 0) {
        return 0;
    }
    *size = (size_t)length;
    return 1;
}

static int parse_frame(char *cursor, sc_frame_t *frame)
{
    uint64_t id;
    uint64_t extended;
    uint64_t length;
    uint64_t byte_value;
    size_t i;

    if (!parse_u64(next_token(&cursor), 10, &id) || id > UINT32_MAX ||
        !parse_u64(next_token(&cursor), 10, &extended) || extended > 1u ||
        !parse_u64(next_token(&cursor), 10, &length) || length > 64u) {
        return 0;
    }

    memset(frame, 0, sizeof(*frame));
    frame->id = (uint32_t)id;
    frame->flags = (uint8_t)extended;
    frame->len = (uint8_t)length;

    for (i = 0u; i < (size_t)length; ++i) {
        char *token = next_token(&cursor);
        if (token == NULL || strlen(token) != 2u ||
            !parse_u64(token, 16, &byte_value) || byte_value > UINT8_MAX) {
            return 0;
        }
        frame->data[i] = (uint8_t)byte_value;
    }
    return next_token(&cursor) == NULL;
}

static int check_expectation(char *cursor, uint16_t signal_count,
                             unsigned line_number)
{
    uint64_t slot;
    uint64_t expected_raw;
    uint64_t expected_flags;
    uint64_t actual_raw;
    uint32_t actual_flags;

    if (!parse_u64(next_token(&cursor), 10, &slot) ||
        slot >= signal_count ||
        !parse_u64(next_token(&cursor), 10, &expected_raw) ||
        !parse_u64(next_token(&cursor), 10, &expected_flags) ||
        expected_flags > UINT32_MAX || next_token(&cursor) != NULL) {
        printf("FAIL %u malformed expectation\n", line_number);
        return 0;
    }

    actual_raw = pool[slot].raw;
    actual_flags = pool[slot].flags;
    if (actual_raw == expected_raw && actual_flags == (uint32_t)expected_flags) {
        printf("OK %u\n", line_number);
        return 1;
    }

    printf("FAIL %u slot=%" PRIu64 " got=%" PRIu64 " want=%" PRIu64
           " flags=%" PRIu32 " want_flags=%" PRIu64 "\n",
           line_number, slot, actual_raw, expected_raw, actual_flags,
           expected_flags);
    return 0;
}

int main(int argc, char **argv)
{
    schema_storage_t schema_storage;
    sc_schema_t *schema = (sc_schema_t *)(void *)schema_storage.bytes;
    sc_frame_t frame;
    FILE *vectors;
    char line[LINE_CAPACITY];
    size_t image_size;
    uint16_t signal_count;
    unsigned line_number = 0u;
    unsigned failures = 0u;
    int have_frame = 0;
    sc_status_t status;

    if (argc != 3) {
        fprintf(stderr, "usage: diff_harness <image.scimg> <vectors.txt>\n");
        return 2;
    }
    if (sc_schema_size() > sizeof(schema_storage.bytes)) {
        fprintf(stderr, "schema storage is too small\n");
        return 2;
    }
    if (!load_image(argv[1], &image_size)) {
        fprintf(stderr, "could not read image: %s\n", argv[1]);
        return 2;
    }

    memset(&schema_storage, 0, sizeof(schema_storage));
    status = sc_schema_open(schema, image, image_size);
    if (status != SC_OK) {
        fprintf(stderr, "could not open image: status=%d\n", (int)status);
        return 2;
    }
    signal_count = sc_schema_signal_count(schema);

    vectors = fopen(argv[2], "r");
    if (vectors == NULL) {
        fprintf(stderr, "could not read vectors: %s\n", argv[2]);
        return 2;
    }

    while (fgets(line, sizeof(line), vectors) != NULL) {
        char *cursor = line;
        char *kind;
        ++line_number;

        while (*cursor == ' ' || *cursor == '\t') {
            ++cursor;
        }
        if (*cursor == '\0' || *cursor == '\r' || *cursor == '\n' ||
            *cursor == '#') {
            continue;
        }

        kind = next_token(&cursor);
        if (kind != NULL && strcmp(kind, "F") == 0) {
            memset(pool, 0, sizeof(pool));
            if (!parse_frame(cursor, &frame)) {
                printf("FAIL %u malformed frame\n", line_number);
                ++failures;
                have_frame = 0;
                continue;
            }
            status = sc_decode(schema, &frame, pool, signal_count);
            if (status != SC_OK) {
                printf("FAIL %u decode status=%d\n", line_number, (int)status);
                ++failures;
                have_frame = 0;
                continue;
            }
            have_frame = 1;
        } else if (kind != NULL && strcmp(kind, "E") == 0 && have_frame) {
            if (!check_expectation(cursor, signal_count, line_number)) {
                ++failures;
            }
        } else {
            printf("FAIL %u expectation without frame or unknown record\n",
                   line_number);
            ++failures;
        }
    }

    if (ferror(vectors) != 0 || fclose(vectors) != 0) {
        fprintf(stderr, "could not finish reading vectors\n");
        return 2;
    }
    return failures == 0u ? 0 : 1;
}
