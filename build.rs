fn main() {
    embed_resource::compile("SandS.rc", embed_resource::NONE)
        .manifest_optional()
        .unwrap();
}
