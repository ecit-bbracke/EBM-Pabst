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

---

## Document Deletion & Lifecycle

### Deleting a Single Document
Users can delete specific documents directly from the Web UI (`documents.html`) using the trash icon, or programmatically via the API:

```http
DELETE /api/documents/{id}
```

When a document is deleted:
1. The physical PDF file is permanently removed from the `uploads/` directory on disk.
2. All associated vector chunks and embeddings are purged from the Qdrant collection via `DeleteDocumentChunksAsync`.
3. The document entry is removed from the SQLite database (`Documents` table).

### Document Overwrite on Re-upload
If a file with an existing `documentId`, `SourceDocumentId`, or filename is re-uploaded:
* The system automatically identifies the duplicate.
* The previous physical file is deleted.
* Old chunks in Qdrant are purged before new chunks are inserted.
* The SQLite metadata record is updated in place, preventing duplicate document catalog entries.

---

## Startup Data Seeding (`SEED_DATA`)

By default, the Worker service does **not** ingest the bundled sample PDFs in `data/Generelt/` on container startup:

```env
SEED_DATA=false
```

To enable automatic ingestion of the sample PDFs when spinning up a fresh environment, set `SEED_DATA=true` in your `.env` file or container environment.

---

## Server Data Reset & Disk Cleanup

If the production server runs low on disk space due to Docker build cache, old container logs, or unwanted uploaded data, run the one-click cleanup script:

### From Windows (PowerShell):
```powershell
.\slet_alle_data_på_server.ps1
```

### Directly on the Linux server (Bash):
```bash
cd /opt/document-rag-system
./slet_alle_data_på_server.sh
```

What the script performs:
1. Stops containers and deletes data volumes (`docker compose down -v`), wiping `uploads_data`, `qdrant_data`, and `database_data`.
2. Prunes all unused Docker images and build caches (`docker system prune -af`), typically reclaiming 5–15 GB of disk space.
3. Truncates any oversized Docker container JSON logs.
4. Displays before-and-after disk space (`df -h /`).
5. Restarts the system in a clean state (`docker compose up -d`).

