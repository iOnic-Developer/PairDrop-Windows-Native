💧 PairDrop: Native
A native Windows system tray client and optimized Docker deployment for PairDrop, allowing seamless local file and clipboard sharing across your devices.
💡 Overview: This repository contains everything you need to run PairDrop with a background Windows client and a proper WebSocket-enabled Unraid backend.
✨ Features
Context Menu Integration: Right-click any file or right-click selected text to send it directly to other devices.
Instant Clipboard: Received text is automatically copied straight to your clipboard.
Always Ready: File transfers are permanently set to auto-accept for frictionless sharing.
Audio Notifications: Plays a ping sound when you receive files or clipboard data.
System Tray App: Runs quietly in the background without cluttering your taskbar.
🚀 Installation
1. Server Side (Unraid / Docker)
We use a custom NGINX gateway to route PairDrop traffic correctly and handle WebSockets.
Step 1: Create the NGINX configuration
mkdir -p /mnt/user/appdata/pairdrop-gateway

cat > /mnt/user/appdata/pairdrop-gateway/default.conf <<'EOF'
server {
    listen 80;

    location / {
        proxy_pass http://pairdrop:3000;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header X-Forwarded-For "";
        proxy_set_header X-Real-IP "";
        proxy_set_header CF-Connecting-IP "";
        proxy_read_timeout 3600;
        proxy_send_timeout 3600;
    }
}
EOF
Step 2: Deploy the docker-compose.yml
services:
  pairdrop:
    image: lscr.io/linuxserver/pairdrop:latest
    container_name: pairdrop
    restart: unless-stopped
    environment:
      - PUID=99
      - PGID=100
      - TZ=Europe/London
      - WS_FALLBACK=true
      - RATE_LIMIT=false
      - DEBUG_MODE=true
    networks:
      - pairdrop-net

  pairdrop-gateway:
    image: nginx:alpine
    container_name: pairdrop-gateway
    restart: unless-stopped
    depends_on:
      - pairdrop
    volumes:
      - /mnt/user/appdata/pairdrop-gateway/default.conf:/etc/nginx/conf.d/default.conf:ro
    ports:
      - 3077:80
    networks:
      - pairdrop-net

networks:
  pairdrop-net:
    driver: bridge
2. Windows Client Side
You can either download the pre-compiled release build, or build it yourself.
Option A: Download Release (Recommended)
Download the latest release from the Releases tab.
Extract and run PairDropTray.exe.
Option B: Build from Source
Extract the source code to C:\PairDropTray
Open PowerShell as Administrator and run:
cd C:\PairDropTray
powershell -ExecutionPolicy Bypass -File .\build.ps1
.\publish\PairDropTray.exe
🌍 Global Access (Cloudflare Tunnels)
Want to access your PairDrop instance securely from anywhere while keeping the native Windows app working locally?
Expose with Cloudflare: Point a URL to your Unraid server using a Cloudflare tunnel (e.g., share.yourdomain.app).
Secure it (Zero Trust): Add a Cloudflare Access policy. The easiest method is to require Google Sign-In, restricted to your specific email address, with a 1-month session duration. You only have to log in once a month.
Mobile Access: On your phone, visit the public URL and sign in via Google.
Windows App Config: Set the Windows app to use your local IP (e.g., http://192.168.0.111:3077). This allows your desktop to bypass the Cloudflare authentication block while remaining securely connected inside your local network.
