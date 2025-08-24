namespace Generator

open System.IO
open Generator.Ir
open Generator.Config

module Registry =

    let generateRegistryFiles (ir: Ir) (outputPath: string) (config: Config) =
        let registryHPath = Path.Combine(outputPath, "include", "registry.h")
        let registryCPath = Path.Combine(outputPath, "src", "registry.c")

        let registryHContent =
            "#ifndef REGISTRY_H\n#define REGISTRY_H\n\n#include <stdint.h>\n#include <stdbool.h>\n\nbool decode_message(uint32_t id, const uint8_t data[], uint8_t dlc, void* msg);\n\n#endif // REGISTRY_H"
        File.WriteAllText(registryHPath, registryHContent)

        let includes =
            ir.Messages
            |> List.map (fun m -> sprintf "#include \"%s.h\"" (m.Name.ToLowerInvariant()))
            |> String.concat "\n"

        let body =
            if config.Dispatch.ToLowerInvariant() = "direct_map" then
                let cases =
                    ir.Messages
                    |> List.map (fun m ->
                        sprintf "        case %du: return %s_decode((%s_t*)msg, data, dlc);" (int m.Id) m.Name m.Name)
                    |> String.concat "\n"
                sprintf "bool decode_message(uint32_t id, const uint8_t data[], uint8_t dlc, void* msg) {\n    switch (id) {\n%s\n        default: return false;\n    }\n}" cases
            else
                let sorted = ir.Messages |> List.sortBy (fun m -> m.Id)
                let entries =
                    sorted
                    |> List.map (fun m -> sprintf "    { %du, (decode_func_t)%s_decode }" (int m.Id) m.Name)
                    |> String.concat ",\n"
                let table =
                    sprintf "typedef bool (*decode_func_t)(void* msg, const uint8_t data[], uint8_t dlc);\n\ntypedef struct { uint32_t id; decode_func_t func; } decoder_entry_t;\n\nstatic const decoder_entry_t decoders[] = {\n%s\n};\n" entries
                let search =
                    "bool decode_message(uint32_t id, const uint8_t data[], uint8_t dlc, void* msg) {\n    int low = 0;\n    int high = (int)(sizeof(decoders) / sizeof(decoder_entry_t)) - 1;\n    while (low <= high) {\n        int mid = low + (high - low) / 2;\n        if (decoders[mid].id == id) {\n            return decoders[mid].func(msg, data, dlc);\n        }\n        if (decoders[mid].id < id) low = mid + 1; else high = mid - 1;\n    }\n    return false;\n}\n"
                table + search

        let finalC = "#include <stdint.h>\n#include <stdbool.h>\n#include \"registry.h\"\n" + includes + "\n\n" + body
        File.WriteAllText(registryCPath, finalC)
        ()