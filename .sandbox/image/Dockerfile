# Base disk image for agent sandboxes (ACA Sandboxes).
# Built remotely with: aca sandboxgroup disk create --source .sandbox/image --name <name>
# Keep it stack-agnostic; stack runtimes go in a derived image or via bootstrap.
#
# Base: official Ubuntu image via the MCR mirror (no Docker Hub anonymous pull limits
# on the remote builder), pinned by digest for reproducible builds. Dependabot
# (.github/dependabot.yml) proposes digest bumps as reviewed PRs.
FROM mcr.microsoft.com/mirror/docker/library/ubuntu:24.04@sha256:c35e29c9450151419d9448b0fd75374fec4fff364a27f176fb458d472dfc9e54

ARG USERNAME=dev
ARG USER_UID=1000
# Pinned Claude Code release; bump deliberately and rebuild the disk image.
ARG CLAUDE_CODE_VERSION=2.1.322
# Pinned VS Code standalone CLI (for `code tunnel`). Get version + sha256 from
# https://update.code.visualstudio.com/api/versions/<version>/cli-linux-x64/stable
# The pin is only the starting version: the CLI self-updates in place (see below).
ARG VSCODE_CLI_VERSION=1.148.0
ARG VSCODE_CLI_SHA256=a3ea5f932f6b4b20e880649e2cdce21eb6025278a95afe8e6cee5ca70a2eba51

ENV DEBIAN_FRONTEND=noninteractive \
    LANG=C.UTF-8

# OS packages + GitHub CLI (official apt repo) + Node.js LTS (needed by many agent tools), one layer.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates curl wget gnupg git git-lfs sudo less vim-tiny jq unzip zip \
      bash-completion procps iproute2 iputils-ping dnsutils openssh-client \
      build-essential python3 python3-venv python3-pip \
 && install -d -m 0755 /etc/apt/keyrings \
 && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg -o /etc/apt/keyrings/githubcli.gpg \
 && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli.gpg] https://cli.github.com/packages stable main" > /etc/apt/sources.list.d/github-cli.list \
 && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
 && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_22.x nodistro main" > /etc/apt/sources.list.d/nodesource.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends gh nodejs \
 && rm -rf /var/lib/apt/lists/*

# Non-root user (ubuntu:24.04 ships an "ubuntu" user with UID 1000; replace it).
RUN userdel -r ubuntu 2>/dev/null || true \
 && useradd -m -s /bin/bash -u ${USER_UID} ${USERNAME} \
 && echo "${USERNAME} ALL=(ALL) NOPASSWD:ALL" > /etc/sudoers.d/${USERNAME} \
 && chmod 0440 /etc/sudoers.d/${USERNAME} \
 && ln -s /home/${USERNAME}/.local/bin/code /usr/local/bin/code

# Claude Code (native installer, per-user), pinned; fail the build if the version differs.
USER ${USERNAME}
RUN curl -fsSL https://claude.ai/install.sh | bash -s ${CLAUDE_CODE_VERSION} \
 && /home/${USERNAME}/.local/bin/claude --version | grep -q "^${CLAUDE_CODE_VERSION} "

# VS Code CLI (Remote Tunnels), checksum-verified. Installed per-user so the CLI's
# own self-update (VS Code's "Update" prompt) can replace the binary in place; in
# root-owned /usr/local/bin the swap fails silently and the prompt keeps returning.
RUN curl -fsSL -o /tmp/vscode-cli.tar.gz \
      "https://update.code.visualstudio.com/${VSCODE_CLI_VERSION}/cli-linux-x64/stable" \
 && echo "${VSCODE_CLI_SHA256}  /tmp/vscode-cli.tar.gz" | sha256sum -c - \
 && tar -xzf /tmp/vscode-cli.tar.gz -C /home/${USERNAME}/.local/bin code \
 && rm /tmp/vscode-cli.tar.gz \
 && /home/${USERNAME}/.local/bin/code --version
ENV PATH="/home/${USERNAME}/.local/bin:${PATH}"

COPY --chmod=0755 bootstrap.sh /usr/local/bin/sandbox-bootstrap

WORKDIR /home/${USERNAME}
# A sandbox needs a long-running main process.
CMD ["sleep", "infinity"]
