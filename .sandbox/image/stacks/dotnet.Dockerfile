# --- stack: dotnet ------------------------------------------------------------
# Appended to image/Dockerfile by `sbx.sh image dotnet` (disk image agent-dotnet-vN).
# OS-level tooling for .NET + Slidev/Playwright repos (e.g. bmeviaumb11/skalazhato-ea).
# Project dependencies (npm ci, dotnet restore, browser download) stay in the repo's hook.
USER root

# .NET SDK (channel pinned; dotnet-install.sh is Microsoft's official installer).
ARG DOTNET_CHANNEL=10.0
ENV DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --channel ${DOTNET_CHANNEL} --install-dir ${DOTNET_ROOT} \
 && ln -s ${DOTNET_ROOT}/dotnet /usr/local/bin/dotnet \
 && rm /tmp/dotnet-install.sh \
 && dotnet --info | head -5

# Node 24 (replaces the base image's Node 22) + EMF/PDF tools.
RUN sed -i 's#node_22.x#node_24.x#' /etc/apt/sources.list.d/nodesource.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends nodejs emf2svg poppler-utils \
 && rm -rf /var/lib/apt/lists/* \
 && node --version

# Playwright Chromium system libraries (the browser binary itself is a project dep,
# downloaded by the repo's hook into the persistent volume).
RUN npx -y playwright-core install-deps chromium \
 && rm -rf /var/lib/apt/lists/* /root/.npm

# csharp-ls (C# language server for the csharp-lsp Claude Code plugin), per user.
USER dev
ENV PATH="/home/dev/.dotnet/tools:${PATH}"
RUN dotnet tool install --global csharp-ls
