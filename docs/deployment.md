# Ubuntu Linux Docker Deployment Guide (Self-Contained)

This guide provides instructions for deploying the Document RAG System to an **Ubuntu Linux** server inside **Docker** using a **Self-Contained Deployment (SCD)** model.

---

## 1. Understanding Self-Contained Docker Deployments

In a standard .NET container deployment, the target runtime image (such as `mcr.microsoft.com/dotnet/aspnet:10.0` or `mcr.microsoft.com/dotnet/runtime:10.0`) must be downloaded and run in the container.

In contrast, our **Self-Contained Deployment (SCD)**:
* Compiles and publishes the application targeting the `linux-x64` platform.
* Bundles the **entire .NET Runtime and ASP.NET Core framework libraries** directly inside the application executable.
* Uses the ultra-minimal base image `mcr.microsoft.com/dotnet/runtime-deps:10.0`, which only contains the OS-level native dependencies (like OpenSSL, GSS, ICU) required by .NET, and **does not** include the .NET runtime itself.
* Eliminates the need to install any .NET runtime on either the Ubuntu host or inside the runtime container, resulting in optimal compatibility and isolation.

---

## 2. Architecture Overview

```
                        +---------------------------------------+
                        |         Ubuntu Host Server            |
                        |                                       |
                        |   +-------------------------------+   |
                        |   |        Docker Compose         |   |
                        |   |                               |   |
                        |   |  +-------------------------+  |   |
  Public / Private      |   |  |     webapi (Ports)      |  |   |
  HTTP Traffic -------->|-->|  |  [8080:8080, 8081:8081] |  |   |
                        |   |  +------------+------------+  |   |
                        |   |               | (gRPC)        |   |
                        |   |  +------------v------------+  |   |
                        |   |  |     worker service      |  |   |
                        |   |  +------------+------------+  |   |
                        |   |               |               |   |
                        |   |  +------------v------------+  |   |
                        |   |  |    qdrant vector db     |  |   |
                        |   |  |       [Port 6333/6334]  |  |   |
                        |   |  +-------------------------+  |   |
                        |   +-------------------------------+   |
                        +---------------------------------------+
```

* **webapi**: Exposes REST endpoints on port `8080` (HTTP) and `8081` (HTTPS/gRPC/Management).
* **worker**: Runs background periodic check-ups and in-memory document queues.
* **qdrant**: Open-source vector database storing processed document embeddings.
* **Shared Volumes**:
  * `qdrant_data`: Persists vector indexes across container recreations.
  * `uploads_data`: A shared mount at `/app/uploads` in both `webapi` and `worker` to ensure uploaded PDFs are synced, persistent, and accessible to both services.

---

## 3. Prerequisites on Ubuntu Server

Log in to your Ubuntu server and ensure Docker and Docker Compose are installed:

```bash
# Update system packages
sudo apt update && sudo apt upgrade -y

# Install Docker
sudo apt install docker.io -y

# Start and enable Docker daemon
sudo systemctl enable --now docker

# Install Docker Compose V2
sudo apt install docker-compose-v2 -y

# (Optional) Allow running Docker commands without sudo
sudo usermod -aG docker $USER
newgrp docker
```

---

## 4. Preparing Configuration on the Server

### A. Code Transfer
Transfer the codebase to your Ubuntu server directory (e.g., `/opt/document-rag-system/`). You can use Git, SFTP, or rsync:

```bash
rsync -avz --exclude="bin/" --exclude="obj/" --exclude=".git/" --exclude=".venv/" ./user@your-server-ip:/opt/document-rag-system/
```

### B. Environment Variables (`.env`)
Create a `.env` file in `/opt/document-rag-system/` to store application secrets securely. Do not commit this file to version control.

```bash
cd /opt/document-rag-system/
nano .env
```

Add your Gemini API key and any other desired overrides:

```env
# Gemini API Configuration
GEMINI_API_KEY=your_actual_gemini_api_key_here

# Optional: Override the collection name
# Qdrant__CollectionName=production-chunks
```

---

## 5. Building and Launching the Containers

To build the self-contained executables inside the multi-stage Docker build and launch the container ecosystem, execute:

```bash
# Build and run in detached mode
docker compose up --build -d
```

### What happens under the hood?
1. **Multi-Stage Build**:
   * The builder retrieves `mcr.microsoft.com/dotnet/sdk:10.0`.
   * It runs unit tests within the build stage.
   * It executes `dotnet publish -c Release -r linux-x64 --self-contained true -o /app/publish` to compile self-contained, native binaries targeting Ubuntu x64.
2. **Minimal Runtime Assembly**:
   * The runtime containers start using `mcr.microsoft.com/dotnet/runtime-deps:10.0`.
   * The WebApi executable (`./DocumentRagSystem.WebApi`) and Worker executable (`./DocumentRagSystem.Worker`) are copied in and run natively without a system-wide or container-wide .NET runtime dependency.

---

## 6. Verifying Deployment

Verify that all three services are running successfully:

```bash
# List all running containers
docker compose ps

# View real-time container logs
docker compose logs -f
```

You can test WebApi responsiveness using `curl`:

```bash
curl http://localhost:8080/health
```

---

## 7. Shared Storage & Persistence Details

The shared volume setup is defined in your `docker-compose.yml`:
* **Qdrant Storage**: Persisted locally in the Docker volume `qdrant_data` and mapped to `/qdrant/storage`.
* **PDF Document Uploads**: Persisted in the Docker volume `uploads_data` and mapped to `/app/uploads` in both containers.
  * This ensures that when a user uploads a document through the Web API, the file is saved to `/app/uploads`.
  * Since the Worker service also mounts the same volume to `/app/uploads`, any processing, verification, or cleanup performed by the Worker service affects the exact same shared uploads directory.

---

## 8. Managing and Updating the Application

### Stop the Services
To stop the application without deleting stored volume data:
```bash
docker compose down
```

### Wipe Data (Hard Reset)
To stop the services and completely delete all indexed vectors and uploaded files:
```bash
docker compose down -v
```

### Update Code & Re-deploy
Whenever you make updates to the C# source code, pull the changes on the server and rebuild the containers:
```bash
git pull
docker compose up --build -d
```

---

## 9. Production Hardening Checklist (Recommended)

1. **SSL Termination**: 
   Do not expose port `8080` directly to the public internet. Use a reverse proxy like **Nginx** or **Caddy** on the Ubuntu host to handle HTTPS/SSL.
   
   Example minimal **Nginx** server block:
   ```nginx
   server {
       listen 443 ssl;
       server_name rag.example.com;

       ssl_certificate /etc/letsencrypt/live/rag.example.com/fullchain.pem;
       ssl_certificate_key /etc/letsencrypt/live/rag.example.com/privkey.pem;

       location / {
           proxy_pass http://localhost:8080;
           proxy_http_version 1.1;
           proxy_set_header Upgrade $http_upgrade;
           proxy_set_header Connection keep-alive;
           proxy_set_header Host $host;
           proxy_cache_bypass $http_upgrade;
           proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
           proxy_set_header X-Forwarded-Proto $scheme;
       }
   }
   ```
2. **UFW Firewall**:
   Enable Ubuntu's built-in firewall and only expose ports `80` and `443`:
   ```bash
   sudo ufw allow OpenSSH
   sudo ufw allow 'Nginx Full'
   sudo ufw enable
   ```
3. **Log Rotation**:
   Docker container logs can grow indefinitely. Ensure you have log rotation set up in `/etc/docker/daemon.json`:
   ```json
   {
     "log-driver": "json-file",
     "log-opts": {
       "max-size": "10m",
       "max-file": "3"
     }
   }
   ```
   *Remember to run `sudo systemctl restart docker` after editing daemon.json.*
