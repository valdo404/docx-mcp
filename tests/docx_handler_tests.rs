use anyhow::Result;
use docx_mcp::docx_handler::{DocxHandler, DocxStyle, TableData, ImageData};
use tempfile::TempDir;
use std::path::PathBuf;
use pretty_assertions::assert_eq;
use rstest::*;
use chrono::Utc;

fn setup_test_handler() -> (DocxHandler, TempDir) {
    let temp_dir = TempDir::new().unwrap();
    let handler = DocxHandler::new().unwrap();
    (handler, temp_dir)
}

#[fixture]
fn handler_and_doc() -> (DocxHandler, String, TempDir) {
    let (mut handler, temp_dir) = setup_test_handler();
    let doc_id = handler.create_document().unwrap();
    (handler, doc_id, temp_dir)
}

#[test]
fn test_create_document() {
    let (mut handler, _temp_dir) = setup_test_handler();
    
    let doc_id = handler.create_document().unwrap();
    assert!(!doc_id.is_empty());
    
    // Document should be in the handler's registry
    assert!(handler.documents.contains_key(&doc_id));
    
    let metadata = handler.get_metadata(&doc_id).unwrap();
    assert_eq!(metadata.id, doc_id);
    assert!(metadata.path.exists());
}

#[test]
fn test_add_paragraph() {
    let (mut handler, doc_id, _temp_dir) = handler_and_doc();
    
    let result = handler.add_paragraph(&doc_id, "Test paragraph", None);
    assert!(result.is_ok());
    
    // Verify content was added by extracting text
    let text = handler.extract_text(&doc_id).unwrap();
    assert!(text.contains("Test paragraph"));
}

#[test]
fn test_add_paragraph_with_style() {
    let (mut handler, doc_id, _temp_dir) = handler_and_doc();
    
    let style = DocxStyle {
        font_family: Some("Arial".to_string()),
        font_size: Some(14),
        bold: Some(true),
        italic: Some(false),
        underline: Some(false),
        color: Some("#FF0000".to_string()),
        alignment: Some("center".to_string()),
        line_spacing: Some(1.5),
    };
    
    let result = handler.add_paragraph(&doc_id, "Styled paragraph", Some(style));
    assert!(result.is_ok());
    
    let text = handler.extract_text(&doc_id).unwrap();
    assert!(text.contains("Styled paragraph"));
}

#[test]
fn test_add_heading() {
    let (mut handler, doc_id, _temp_dir) = handler_and_doc();
    
    for level in 1..=6 {
        let heading_text = format!("Heading Level {}", level);
        let result = handler.add_heading(&doc_id, &heading_text, level);
        assert!(result.is_ok(), "Failed to add heading level {}", level);
        
        let text = handler.extract_text(&doc_id).unwrap();
        assert!(text.contains(&heading_text));
    }
}

#[test]
fn test_add_table() {
    let (mut handler, doc_id, _temp_dir) = handler_and_doc();
    
    let table_data = TableData {
        rows: vec![
            vec!["Name".to_string(), "Age".to_string(), "City".to_string()],
            vec!["John".to_string(), "30".to_string(), "NYC".to_string()],
            vec!["Jane".to_string(), "25".to_string(), "LA".to_string()],
        ],
        headers: Some(vec!["Name".to_string(), "Age".to_string(), "City".to_string()]),
        border_style: Some("single".to_string()),
        col_widths: None,
        merges: None,
        cell_shading: None,
    };
    
    let result = handler.add_table(&doc_id, table_data);
    assert!(result.is_ok());
    
    let text = handler.extract_text(&doc_id).unwrap();
    assert!(text.contains("John"));
    assert!(text.contains("Jane"));
    assert!(text.contains("NYC"));
}

#[test]
fn test_add_list() {
    let (mut handler, doc_id, _temp_dir) = handler_and_doc();
    
    let items = vec![
        "First item".to_string(),
        "Second item".to_string(),
        "Third item".to_string(),
    ];
    
    // Test unordered list
    let result = handler.add_list(&doc_id, items.clone(), false);
    assert!(result.is_ok());
    
    // Test ordered list
    let result = handler.add_list(&doc_id, items.clone(), true);
    assert!(result.is_ok());
    
    let text = handler.extract_text(&doc_id).unwrap();
    assert!(text.contains("First item"));
    assert!(text.contains("Second item"));
    assert!(text.contains("Third item"));
}

#[test]
fn test_set_header_footer() {
    let (mut handler, doc_id, _temp_dir) = handler_and_doc();
    
    let header_result = handler.set_header(&doc_id, "Document Header");
    assert!(header_result.is_ok());
    
    let footer_result = handler.set_footer(&doc_id, "Document Footer");
    assert!(footer_result.is_ok());
}

#[test]
fn test_add_page_break() {
    let (mut handler, doc_id, _temp_dir) = handler_and_doc();
    
    handler.add_paragraph(&doc_id, "Before page break", None).unwrap();
    
    let result = handler.add_page_break(&doc_id);
    assert!(result.is_ok());
    
    handler.add_paragraph(&doc_id, "After page break", None).unwrap();
    
    let text = handler.extract_text(&doc_id).unwrap();
    assert!(text.contains("Before page break"));
    assert!(text.contains("After page break"));
}

#[test]
fn test_extract_text_empty_document() {
    let (handler, doc_id, _temp_dir) = handler_and_doc();
    
    let text = handler.extract_text(&doc_id).unwrap();
    // Empty document might have some default content or be truly empty
    assert!(text.is_empty() || text.trim().is_empty());
}

#[test]
fn test_save_and_close_document() {
    let (mut handler, doc_id, temp_dir) = handler_and_doc();
    
    handler.add_paragraph(&doc_id, "Test content", None).unwrap();
    
    let save_path = temp_dir.path().join("test_output.docx");
    let save_result = handler.save_document(&doc_id, &save_path);
    assert!(save_result.is_ok());
    assert!(save_path.exists());
    
    let close_result = handler.close_document(&doc_id);
    assert!(close_result.is_ok());
    assert!(!handler.documents.contains_key(&doc_id));
}

#[test]
fn test_open_existing_document() {
    let (mut handler, doc_id, temp_dir) = handler_and_doc();
    
    // Create and save a document
    handler.add_paragraph(&doc_id, "Original content", None).unwrap();
    let save_path = temp_dir.path().join("existing.docx");
    handler.save_document(&doc_id, &save_path).unwrap();
    handler.close_document(&doc_id).unwrap();
    
    // Open the saved document
    let opened_doc_id = handler.open_document(&save_path).unwrap();
    assert_ne!(opened_doc_id, doc_id); // Should be a new ID
    
    let text = handler.extract_text(&opened_doc_id).unwrap();
    assert!(text.contains("Original content"));
}

#[test]
fn test_list_documents() {
    let (mut handler, _temp_dir) = setup_test_handler();
    
    // Initially should be empty
    let docs = handler.list_documents();
    let initial_count = docs.len();
    
    // Create some documents
    let _doc1 = handler.create_document().unwrap();
    let _doc2 = handler.create_document().unwrap();
    let _doc3 = handler.create_document().unwrap();
    
    let docs = handler.list_documents();
    assert_eq!(docs.len(), initial_count + 3);
}

#[test]
fn test_document_not_found_error() {
    let (handler, _temp_dir) = setup_test_handler();
    
    let fake_id = "nonexistent-document-id";
    
    let result = handler.extract_text(fake_id);
    assert!(result.is_err());
    assert!(result.unwrap_err().to_string().contains("Document not found"));
}

#[test]
fn test_get_metadata() {
    let (handler, doc_id, _temp_dir) = handler_and_doc();
    
    let metadata = handler.get_metadata(&doc_id).unwrap();
    
    assert_eq!(metadata.id, doc_id);
    assert!(metadata.path.exists());
    assert!(metadata.created_at <= Utc::now());
    assert!(metadata.modified_at <= Utc::now());
    assert_eq!(metadata.page_count, Some(1));
    assert_eq!(metadata.word_count, Some(0));
}

#[test]
fn test_concurrent_document_operations() {
    use std::sync::Arc;
    use std::sync::Mutex;
    use std::thread;
    
    let (handler, _temp_dir) = setup_test_handler();
    let handler = Arc::new(Mutex::new(handler));
    
    let handles: Vec<_> = (0..5).map(|i| {
        let handler = Arc::clone(&handler);
        thread::spawn(move || {
            let doc_id = {
                let mut h = handler.lock().unwrap();
                h.create_document().unwrap()
            };
            
            {
                let mut h = handler.lock().unwrap();
                h.add_paragraph(&doc_id, &format!("Thread {} content", i), None).unwrap();
            }
            
            {
                let h = handler.lock().unwrap();
                let text = h.extract_text(&doc_id).unwrap();
                assert!(text.contains(&format!("Thread {} content", i)));
            }
            
            doc_id
        })
    }).collect();
    
    let doc_ids: Vec<_> = handles.into_iter().map(|h| h.join().unwrap()).collect();
    
    // All documents should be different
    let mut unique_ids = doc_ids.clone();
    unique_ids.sort();
    unique_ids.dedup();
    assert_eq!(unique_ids.len(), doc_ids.len());
}

#[test]
fn test_large_document_creation() {
    let (mut handler, doc_id, _temp_dir) = handler_and_doc();
    
    // Add many paragraphs to test performance
    for i in 0..100 {
        let content = format!("Paragraph number {} with some content to make it realistic", i);
        handler.add_paragraph(&doc_id, &content, None).unwrap();
    }
    
    let text = handler.extract_text(&doc_id).unwrap();
    assert!(text.contains("Paragraph number 0"));
    assert!(text.contains("Paragraph number 99"));
    
    // Verify word count (lower threshold due to simplified text extraction)
    let words: Vec<&str> = text.split_whitespace().collect();
    assert!(words.len() > 300);
}

#[test]
fn test_special_characters_in_content() {
    let (mut handler, doc_id, _temp_dir) = handler_and_doc();
    
    let special_content = "Special chars: éñüñdéd, 中文, русский, العربية, 🚀📝✨";
    handler.add_paragraph(&doc_id, special_content, None).unwrap();
    
    let text = handler.extract_text(&doc_id).unwrap();
    assert!(text.contains("éñüñdéd"));
    assert!(text.contains("🚀📝✨"));
}

// ── XML fallback tests ────────────────────────────────────────

/// Helper: create a document with a table, a hyperlink and an image,
/// save it, then re-open via open_document (which does NOT populate in_memory_ops).
fn create_and_reopen_rich_doc() -> (DocxHandler, String, String, TempDir) {
    let (mut handler, temp_dir) = setup_test_handler();
    let doc_id = handler.create_document().unwrap();

    // Add a table
    let table_data = TableData {
        rows: vec![
            vec!["Header1".to_string(), "Header2".to_string()],
            vec!["CellA".to_string(), "CellB".to_string()],
            vec!["CellC".to_string(), "CellD".to_string()],
        ],
        headers: Some(vec!["Header1".to_string(), "Header2".to_string()]),
        border_style: Some("single".to_string()),
        col_widths: None,
        merges: None,
        cell_shading: None,
    };
    handler.add_table(&doc_id, table_data).unwrap();

    // Add a hyperlink
    handler.add_hyperlink(&doc_id, "Rust website", "https://www.rust-lang.org").unwrap();

    // Add a small 1x1 PNG image
    let png_data = create_minimal_png();
    let image = ImageData {
        data: png_data,
        width: Some(50),
        height: Some(50),
        alt_text: Some("test image".to_string()),
    };
    handler.add_image(&doc_id, image).unwrap();

    // Save to a file on disk
    let save_path = temp_dir.path().join("rich_doc.docx");
    handler.save_document(&doc_id, &save_path).unwrap();

    // Close the original document
    handler.close_document(&doc_id).unwrap();

    // Re-open via open_document (XML-only path, no in_memory_ops)
    let opened_id = handler.open_document(&save_path).unwrap();

    (handler, doc_id, opened_id, temp_dir)
}

/// Create a minimal valid 1x1 white PNG in memory.
fn create_minimal_png() -> Vec<u8> {
    // Minimal valid PNG: 1x1 pixel, RGBA white
    let mut buf = Vec::new();
    // PNG signature
    buf.extend_from_slice(&[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    // IHDR chunk
    let ihdr_data: [u8; 13] = [
        0, 0, 0, 1, // width = 1
        0, 0, 0, 1, // height = 1
        8,          // bit depth = 8
        2,          // color type = RGB
        0,          // compression
        0,          // filter
        0,          // interlace
    ];
    let ihdr_crc = crc32(&[b'I', b'H', b'D', b'R'], &ihdr_data);
    buf.extend_from_slice(&(13u32).to_be_bytes()); // length
    buf.extend_from_slice(b"IHDR");
    buf.extend_from_slice(&ihdr_data);
    buf.extend_from_slice(&ihdr_crc.to_be_bytes());

    // IDAT chunk: zlib-compressed scanline (filter=0, R=255, G=255, B=255)
    let raw_scanline: [u8; 4] = [0, 255, 255, 255]; // filter byte + RGB
    let compressed = deflate_raw(&raw_scanline);
    let idat_crc = crc32(b"IDAT", &compressed);
    buf.extend_from_slice(&(compressed.len() as u32).to_be_bytes());
    buf.extend_from_slice(b"IDAT");
    buf.extend_from_slice(&compressed);
    buf.extend_from_slice(&idat_crc.to_be_bytes());

    // IEND chunk
    let iend_crc = crc32(b"IEND", &[]);
    buf.extend_from_slice(&0u32.to_be_bytes());
    buf.extend_from_slice(b"IEND");
    buf.extend_from_slice(&iend_crc.to_be_bytes());
    buf
}

fn crc32(chunk_type: &[u8], data: &[u8]) -> u32 {
    let mut crc: u32 = 0xFFFFFFFF;
    for &byte in chunk_type.iter().chain(data.iter()) {
        crc ^= byte as u32;
        for _ in 0..8 {
            if crc & 1 != 0 {
                crc = (crc >> 1) ^ 0xEDB88320;
            } else {
                crc >>= 1;
            }
        }
    }
    crc ^ 0xFFFFFFFF
}

fn deflate_raw(input: &[u8]) -> Vec<u8> {
    // Minimal zlib: CMF=0x78, FLG=0x01 (no dict, check bits), then a stored block
    let mut out = vec![0x78, 0x01];
    // DEFLATE stored block: BFINAL=1, BTYPE=00
    out.push(0x01); // final block, stored
    let len = input.len() as u16;
    out.extend_from_slice(&len.to_le_bytes());
    out.extend_from_slice(&(!len).to_le_bytes());
    out.extend_from_slice(input);
    // Adler-32 checksum
    let adler = adler32(input);
    out.extend_from_slice(&adler.to_be_bytes());
    out
}

fn adler32(data: &[u8]) -> u32 {
    let mut a: u32 = 1;
    let mut b: u32 = 0;
    for &byte in data {
        a = (a + byte as u32) % 65521;
        b = (b + a) % 65521;
    }
    (b << 16) | a
}

#[test]
fn test_xml_fallback_get_tables() {
    let (handler, _orig_id, opened_id, _temp_dir) = create_and_reopen_rich_doc();

    let result = handler.get_tables_json(&opened_id);
    assert!(result.is_ok(), "get_tables_json failed: {:?}", result.err());

    let val = result.unwrap();
    let tables = val["tables"].as_array().expect("tables should be an array");
    assert!(!tables.is_empty(), "Should find at least one table");

    let t0 = &tables[0];
    assert_eq!(t0["rows"].as_u64().unwrap(), 3, "Table should have 3 rows");
    assert_eq!(t0["cols"].as_u64().unwrap(), 2, "Table should have 2 columns");

    // Verify cell content
    let cells = t0["cells"].as_array().expect("cells should be an array");
    let first_row = cells[0].as_array().expect("first row should be an array");
    assert!(first_row[0].as_str().unwrap().contains("Header1"), "First cell should contain Header1");
    let second_row = cells[1].as_array().expect("second row should be an array");
    assert!(second_row[0].as_str().unwrap().contains("CellA"), "Cell (1,0) should contain CellA");
}

#[test]
fn test_xml_fallback_list_hyperlinks() {
    let (handler, _orig_id, opened_id, _temp_dir) = create_and_reopen_rich_doc();

    let result = handler.list_hyperlinks(&opened_id);
    assert!(result.is_ok(), "list_hyperlinks failed: {:?}", result.err());

    let val = result.unwrap();
    let links = val["hyperlinks"].as_array().expect("hyperlinks should be an array");
    assert!(!links.is_empty(), "Should find at least one hyperlink");

    let link0 = &links[0];
    assert!(
        link0["text"].as_str().unwrap().contains("Rust website"),
        "Hyperlink text should contain 'Rust website', got: {}",
        link0["text"]
    );
    assert!(
        link0["url"].as_str().unwrap().contains("rust-lang.org"),
        "Hyperlink URL should contain 'rust-lang.org', got: {}",
        link0["url"]
    );
}

#[test]
fn test_xml_fallback_list_images() {
    let (handler, _orig_id, opened_id, _temp_dir) = create_and_reopen_rich_doc();

    let result = handler.list_images(&opened_id);
    assert!(result.is_ok(), "list_images failed: {:?}", result.err());

    let val = result.unwrap();
    let images = val["images"].as_array().expect("images should be an array");
    assert!(!images.is_empty(), "Should find at least one image");

    let img0 = &images[0];
    // The image should have dimensions (from EMU conversion)
    assert!(img0["width"].as_u64().is_some(), "Image should have width");
    assert!(img0["height"].as_u64().is_some(), "Image should have height");
}

#[test]
fn test_xml_fallback_empty_document() {
    // Open an empty document via open_document — all three methods should return empty arrays, not errors.
    let (mut handler, temp_dir) = setup_test_handler();
    let doc_id = handler.create_document().unwrap();
    let save_path = temp_dir.path().join("empty.docx");
    handler.save_document(&doc_id, &save_path).unwrap();
    handler.close_document(&doc_id).unwrap();

    let opened_id = handler.open_document(&save_path).unwrap();

    let tables = handler.get_tables_json(&opened_id).unwrap();
    assert_eq!(tables["tables"].as_array().unwrap().len(), 0);

    let images = handler.list_images(&opened_id).unwrap();
    assert_eq!(images["images"].as_array().unwrap().len(), 0);

    let links = handler.list_hyperlinks(&opened_id).unwrap();
    assert_eq!(links["hyperlinks"].as_array().unwrap().len(), 0);
}

#[test]
fn test_in_memory_ops_still_work() {
    // Ensure the in-memory path is still preferred when available (no regression).
    let (mut handler, temp_dir) = setup_test_handler();
    let doc_id = handler.create_document().unwrap();

    let table_data = TableData {
        rows: vec![
            vec!["A".to_string(), "B".to_string()],
            vec!["C".to_string(), "D".to_string()],
        ],
        headers: None,
        border_style: None,
        col_widths: None,
        merges: None,
        cell_shading: None,
    };
    handler.add_table(&doc_id, table_data).unwrap();
    handler.add_hyperlink(&doc_id, "Example", "https://example.com").unwrap();

    let tables = handler.get_tables_json(&doc_id).unwrap();
    assert!(!tables["tables"].as_array().unwrap().is_empty());

    let links = handler.list_hyperlinks(&doc_id).unwrap();
    assert!(!links["hyperlinks"].as_array().unwrap().is_empty());
}