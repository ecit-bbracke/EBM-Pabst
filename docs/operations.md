# Operations

## Local Uploads And Document Links

The Web API serves uploaded PDF files under the `/uploads` route. Query citations and document metadata should return URLs such as:

```text
/uploads/<stored-pdf-file-name>.pdf
```

The actual files are stored in local `uploads` folders under the service output directories. During local development, WebApi serves both:

- `src/DocumentRagSystem.WebApi/bin/<Configuration>/net10.0/uploads`
- `src/DocumentRagSystem.Worker/bin/<Configuration>/net10.0/uploads`

This matters because documents may be processed by either the WebApi upload endpoint or the Worker ingestion process.

## Qdrant Metadata

Each vector chunk should include enough payload metadata for the API to build document links after a restart:

- `document_id`
- `file_name`
- `file_path`
- `uploaded_at`

If older vectors only contain `document_id`, the API can still show the document id, but it cannot reliably reconstruct a working PDF link unless a matching file can be found in one of the served upload directories.

## Troubleshooting 404 Document Links

If a citation link opens as 404:

1. Restart the WebApi so it uses the current static-file configuration.
2. Check that the target PDF exists under one of the served `uploads` directories.
3. If the document was indexed before file metadata was stored in Qdrant, reprocess the PDF or migrate the vector payload metadata.
4. Confirm the citation response contains a `filepath` value beginning with `/uploads/`.
