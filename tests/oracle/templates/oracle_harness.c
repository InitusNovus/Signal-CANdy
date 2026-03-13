#include <ctype.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* {{INCLUDES}} */

#define ORACLE_LINE_CAPACITY 8192
#define ORACLE_ERROR_CAPACITY 512
#define ORACLE_PAYLOAD_CAPACITY 64

static const char* skip_whitespace(const char* text) {
    while (*text != '\0' && isspace((unsigned char)*text)) {
        text++;
    }

    return text;
}

static const char* find_key(const char* json, const char* key) {
    char pattern[128];
    size_t key_length = strlen(key);
    if (key_length + 3u > sizeof(pattern)) {
        return NULL;
    }

    sprintf(pattern, "\"%s\"", key);
    return strstr(json, pattern);
}

static bool json_get_string_value(const char* json, const char* key, char* out, size_t out_len) {
    const char* key_pos = find_key(json, key);
    const char* value_start;
    char parsed[256];
    size_t parsed_len;

    if (key_pos == NULL) {
        return false;
    }

    value_start = strchr(key_pos, ':');
    if (value_start == NULL) {
        return false;
    }

    value_start = skip_whitespace(value_start + 1);
    if (sscanf(value_start, "\"%255[^\"]\"", parsed) != 1) {
        return false;
    }

    parsed_len = strlen(parsed);
    if (parsed_len + 1u > out_len) {
        return false;
    }

    memcpy(out, parsed, parsed_len + 1u);
    return true;
}

static bool json_get_int_value(const char* json, const char* key, int* out_value) {
    const char* key_pos = find_key(json, key);
    const char* value_start;
    char parsed[64];

    if (key_pos == NULL) {
        return false;
    }

    value_start = strchr(key_pos, ':');
    if (value_start == NULL) {
        return false;
    }

    value_start = skip_whitespace(value_start + 1);
    if (sscanf(value_start, "%63[^,} \t\r\n]", parsed) != 1) {
        return false;
    }

    *out_value = atoi(parsed);
    return true;
}

static bool json_get_u8_array(const char* json, const char* key, uint8_t* out, size_t out_capacity, uint8_t* out_len) {
    const char* key_pos = find_key(json, key);
    const char* value_start;
    const char* value_end;
    char values[ORACLE_LINE_CAPACITY];
    size_t length;
    size_t count = 0u;
    char* token;

    if (key_pos == NULL) {
        return false;
    }

    value_start = strchr(key_pos, '[');
    if (value_start == NULL) {
        return false;
    }

    value_end = strchr(value_start, ']');
    if (value_end == NULL || value_end < value_start) {
        return false;
    }

    length = (size_t)(value_end - value_start - 1);
    if (length + 1u > sizeof(values)) {
        return false;
    }

    memcpy(values, value_start + 1, length);
    values[length] = '\0';

    token = strtok(values, ",");
    while (token != NULL) {
        const char* cursor = skip_whitespace(token);
        int parsed_value = atoi(cursor);

        if (count >= out_capacity || parsed_value < 0 || parsed_value > 255) {
            return false;
        }

        out[count] = (uint8_t)parsed_value;
        count++;
        token = strtok(NULL, ",");
    }

    *out_len = (uint8_t)count;
    return true;
}

static bool json_get_object_value(const char* json, const char* key, char* out, size_t out_len) {
    const char* key_pos = find_key(json, key);
    const char* value_start;
    const char* cursor;
    const char* value_end = NULL;
    size_t length;
    int depth = 0;

    if (key_pos == NULL) {
        return false;
    }

    value_start = strchr(key_pos, '{');
    if (value_start == NULL) {
        return false;
    }

    for (cursor = value_start; *cursor != '\0'; ++cursor) {
        if (*cursor == '{') {
            depth++;
        } else if (*cursor == '}') {
            depth--;
            if (depth == 0) {
                value_end = cursor;
                break;
            }
        }
    }

    if (value_end == NULL) {
        return false;
    }

    length = (size_t)(value_end - value_start + 1);
    if (length + 1u > out_len) {
        return false;
    }

    memcpy(out, value_start, length);
    out[length] = '\0';
    return true;
}

static bool json_get_signal_float(const char* json, const char* key, float* out_value) {
    const char* key_pos = find_key(json, key);
    const char* value_start;
    char parsed[64];

    if (key_pos == NULL) {
        return false;
    }

    value_start = strchr(key_pos, ':');
    if (value_start == NULL) {
        return false;
    }

    value_start = skip_whitespace(value_start + 1);
    if (sscanf(value_start, "%63[^,} \t\r\n]", parsed) != 1) {
        return false;
    }

    *out_value = (float)atof(parsed);
    return true;
}

static bool format_data_array(const uint8_t* data, uint8_t dlc, char* out, size_t out_len) {
    size_t required = 3u;
    size_t offset = 0u;
    int written;

    if (dlc > 0u) {
        required += 3u;
        if (dlc > 1u) {
            required += ((size_t)dlc - 1u) * 4u;
        }
    }

    if (required > out_len) {
        return false;
    }

    written = sprintf(out + offset, "[");
    if (written < 0) {
        return false;
    }

    offset += (size_t)written;

    for (uint8_t index = 0u; index < dlc; ++index) {
        written = sprintf(
            out + offset,
            (index == 0u) ? "%u" : ",%u",
            (unsigned int)data[index]
        );
        if (written < 0) {
            return false;
        }

        offset += (size_t)written;
    }

    written = sprintf(out + offset, "]");
    return written >= 0;
}

static void emit_error_json(const char* error_message) {
    char escaped[ORACLE_ERROR_CAPACITY];
    size_t write_index = 0u;

    for (size_t read_index = 0u; error_message[read_index] != '\0'; ++read_index) {
        char current = error_message[read_index];
        if ((unsigned char)current < 0x20u) {
            continue;
        }

        if ((current == '\\' || current == '"') && write_index + 2u < sizeof(escaped)) {
            escaped[write_index++] = '\\';
            escaped[write_index++] = current;
            continue;
        }

        if (write_index + 1u >= sizeof(escaped)) {
            break;
        }

        escaped[write_index++] = current;
    }

    escaped[write_index] = '\0';
    printf("{\"ok\": false, \"error\": \"%s\"}\n", escaped);
    fflush(stdout);
}

static bool signals_to_json(const char* msg_name, const void* msg_value, char* out, size_t out_len) {
    /* {{SIGNAL_TO_JSON}} */
    (void)msg_name;
    (void)msg_value;
    (void)out;
    (void)out_len;
    return false;
}

static bool json_to_signal(const char* msg_name, const char* signals_json, void* msg_value, char* error, size_t error_len) {
    /* {{JSON_TO_SIGNAL}} */
    (void)msg_value;
    snprintf(error, error_len, "unknown message for signal parsing: %s", msg_name);
    return false;
}

static bool dispatch_decode(
    const char* msg_name,
    const uint8_t* data,
    uint8_t dlc,
    char* out_json,
    size_t out_json_len,
    char* error,
    size_t error_len
) {
    /* {{DECODE_DISPATCH}} */
    (void)data;
    (void)dlc;
    (void)out_json;
    (void)out_json_len;
    snprintf(error, error_len, "unknown message for decode: %s", msg_name);
    return false;
}

static bool dispatch_encode(
    const char* msg_name,
    const char* signals_json,
    uint8_t* data,
    uint8_t* out_dlc,
    char* error,
    size_t error_len
) {
    /* {{ENCODE_DISPATCH}} */
    (void)signals_json;
    (void)data;
    (void)out_dlc;
    snprintf(error, error_len, "unknown message for encode: %s", msg_name);
    return false;
}

int main(void) {
    char line[ORACLE_LINE_CAPACITY];

    while (fgets(line, sizeof(line), stdin) != NULL) {
        char message_name[128];
        char action[32];
        size_t line_len = strlen(line);

        if (line_len > 0u && line[line_len - 1u] == '\n') {
            line[line_len - 1u] = '\0';
        }

        if (line[0] == '\0') {
            continue;
        }

        if (!json_get_string_value(line, "message", message_name, sizeof(message_name))
            || !json_get_string_value(line, "action", action, sizeof(action))) {
            emit_error_json("missing message or action field");
            continue;
        }

        if (strcmp(action, "decode") == 0) {
            uint8_t data[ORACLE_PAYLOAD_CAPACITY];
            uint8_t parsed_len = 0u;
            int dlc_value = 0;
            char out_json[ORACLE_LINE_CAPACITY];
            char error[ORACLE_ERROR_CAPACITY];

            memset(data, 0, sizeof(data));

            if (!json_get_u8_array(line, "data", data, sizeof(data), &parsed_len)) {
                emit_error_json("invalid data array");
                continue;
            }

            if (!json_get_int_value(line, "dlc", &dlc_value)) {
                emit_error_json("missing dlc field");
                continue;
            }

            if (dlc_value < 0 || dlc_value > ORACLE_PAYLOAD_CAPACITY) {
                emit_error_json("dlc out of range");
                continue;
            }

            if (parsed_len < (uint8_t)dlc_value) {
                emit_error_json("data array shorter than dlc");
                continue;
            }

            if (!dispatch_decode(
                message_name,
                data,
                (uint8_t)dlc_value,
                out_json,
                sizeof(out_json),
                error,
                sizeof(error)
            )) {
                emit_error_json(error);
                continue;
            }

            printf("%s\n", out_json);
            fflush(stdout);
            continue;
        }

        if (strcmp(action, "encode") == 0) {
            char signals_json[ORACLE_LINE_CAPACITY];
            uint8_t data[ORACLE_PAYLOAD_CAPACITY];
            uint8_t out_dlc = 0u;
            char data_json[ORACLE_LINE_CAPACITY];
            char out_json[ORACLE_LINE_CAPACITY];
            char error[ORACLE_ERROR_CAPACITY];

            memset(data, 0, sizeof(data));

            if (!json_get_object_value(line, "signals", signals_json, sizeof(signals_json))) {
                emit_error_json("missing signals object");
                continue;
            }

            if (!dispatch_encode(message_name, signals_json, data, &out_dlc, error, sizeof(error))) {
                emit_error_json(error);
                continue;
            }

            if (!format_data_array(data, out_dlc, data_json, sizeof(data_json))) {
                emit_error_json("failed to format encoded payload");
                continue;
            }

            if (snprintf(
                out_json,
                sizeof(out_json),
                "{\"ok\": true, \"data\": %s, \"dlc\": %u}",
                data_json,
                (unsigned int)out_dlc
            ) < 0) {
                emit_error_json("failed to format encode response");
                continue;
            }
            printf("%s\n", out_json);
            fflush(stdout);
            continue;
        }

        emit_error_json("unknown action");
    }

    return 0;
}
