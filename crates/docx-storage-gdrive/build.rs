fn main() -> Result<(), Box<dyn std::error::Error>> {
    // Compile the protobuf definitions
    tonic_build::configure()
        .build_server(true)
        .build_client(false)
        .file_descriptor_set_path(
            std::path::PathBuf::from(std::env::var("OUT_DIR")?).join("storage_descriptor.bin"),
        )
        .compile_protos(&["../../proto/storage.proto"], &["../../proto"])?;
    Ok(())
}
