// Shared modules (used by both the standalone binary and the embedded staticlib)
pub mod config;
pub mod error;
pub mod lock;
pub mod service;
pub mod service_sync;
pub mod service_watch;
pub mod storage;
pub mod sync;
pub mod watch;

// Embedded server support
pub mod embedded;
pub mod server;

/// File descriptor set for gRPC reflection
pub const FILE_DESCRIPTOR_SET: &[u8] = tonic::include_file_descriptor_set!("storage_descriptor");

// =============================================================================
// C FFI entry points for static linking into NativeAOT binaries
// =============================================================================

#[allow(unsafe_code)]
mod ffi {
    use std::ffi::CStr;
    use std::os::raw::c_char;
    use std::path::Path;

    use crate::embedded;

    #[derive(serde::Deserialize)]
    struct InitConfig {
        local_storage_dir: String,
    }

    /// Initialize storage backends and start in-memory gRPC server.
    /// config_json: null-terminated UTF-8 JSON, e.g. {"local_storage_dir": "/path"}
    /// Returns 0 on success, -1 on error.
    #[no_mangle]
    pub extern "C" fn docx_storage_init(config_json: *const c_char) -> i32 {
        if config_json.is_null() {
            return -1;
        }
        let c_str = unsafe { CStr::from_ptr(config_json) };
        let json_str = match c_str.to_str() {
            Ok(s) => s,
            Err(_) => return -1,
        };
        let config: InitConfig = match serde_json::from_str(json_str) {
            Ok(c) => c,
            Err(_) => return -1,
        };
        match embedded::init(Path::new(&config.local_storage_dir)) {
            Ok(()) => 0,
            Err(_) => -1,
        }
    }

    /// Read from the client side of the in-memory gRPC transport.
    /// Returns bytes read (>0), 0 = EOF, -1 = error.
    #[no_mangle]
    pub extern "C" fn docx_pipe_read(buf: *mut u8, max_len: usize) -> i64 {
        if buf.is_null() || max_len == 0 {
            return -1;
        }
        let slice = unsafe { std::slice::from_raw_parts_mut(buf, max_len) };
        embedded::pipe_read(slice)
    }

    /// Write to the client side of the in-memory gRPC transport.
    /// Returns bytes written, -1 = error.
    #[no_mangle]
    pub extern "C" fn docx_pipe_write(buf: *const u8, len: usize) -> i64 {
        if buf.is_null() || len == 0 {
            return 0;
        }
        let slice = unsafe { std::slice::from_raw_parts(buf, len) };
        embedded::pipe_write(slice)
    }

    /// Flush the write side of the transport.
    /// Returns 0 on success, -1 on error.
    #[no_mangle]
    pub extern "C" fn docx_pipe_flush() -> i32 {
        embedded::pipe_flush()
    }

    /// Shutdown the in-memory gRPC server and cleanup.
    /// Returns 0 on success.
    #[no_mangle]
    pub extern "C" fn docx_storage_shutdown() -> i32 {
        embedded::shutdown();
        0
    }
}
