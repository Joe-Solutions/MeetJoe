# MeetJoe production deploy (lean EC2)

## Host

| Item | Value |
| --- | --- |
| Instance | `meetjoe-prod` `i-07e7861b7e2617478` (`t3.small`, Ubuntu 24.04, `ap-southeast-1`) |
| Elastic IP | `13.215.225.98` (`PUBLIC_IP` in `.env`) |
| SSH | `ssh -i barter_keys/barter_key.pem ubuntu@13.215.225.98` |
| App dir | `/opt/meetjoe` |
| Secrets | server `/opt/meetjoe/deploy/prod/.env` + local `barter_keys/meetjoe-prod.env` (not in git) |

## DNS (`joesolutions.ph`) — add these now

| Type | Host | Value |
| --- | --- | --- |
| A | `meetjoe` | `13.215.225.98` → `meetjoe.joesolutions.ph` |
| A | `api.meetjoe` | `13.215.225.98` → `api.meetjoe.joesolutions.ph` |

Caddy retries Let’s Encrypt automatically once the A records resolve. After DNS is live:

```bash
# optional nudge
ssh … 'cd /opt/meetjoe && sg docker -c "docker compose -f deploy/prod/docker-compose.yml --env-file deploy/prod/.env restart caddy"'
```

After DNS propagates, Caddy obtains Let’s Encrypt certs automatically.

## First bring-up

```bash
sudo mkdir -p /opt/meetjoe && sudo chown "$USER:$USER" /opt/meetjoe
# rsync or git clone the repo into /opt/meetjoe

cd /opt/meetjoe
cp deploy/prod/.env.example deploy/prod/.env
# edit secrets + PUBLIC_IP
./deploy/prod/render-config.sh
docker compose -f deploy/prod/docker-compose.yml --env-file deploy/prod/.env up -d --build
```

## Smoke checks

```bash
curl -fsS https://api.meetjoe.joesolutions.ph/health
curl -fsSI https://meetjoe.joesolutions.ph/
docker compose -f deploy/prod/docker-compose.yml --env-file deploy/prod/.env ps
```

Desktop clients: `MEETJOE_API_URL=https://api.meetjoe.joesolutions.ph`

## CI / CD

| Workflow | Trigger | What it does |
| --- | --- | --- |
| `.github/workflows/ci.yml` | PR + push to `main` | Desktop + API tests; Docker image build (no push) |
| `.github/workflows/deploy.yml` | Push to `main` + manual | Build/push `ghcr.io/joe-solutions/meetjoe-api`, SSH to EC2, pull image, restart compose, HTTPS smoke |

Secrets (repo): `MEETJOE_SSH_KEY`, `MEETJOE_HOST`, `MEETJOE_SSH_USER`, `MEETJOE_SG_ID`, `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_REGION`.

Deploy temporarily allowlists the GitHub Actions runner IP on the MeetJoe SG for SSH, then revokes it. Server `/opt/meetjoe/deploy/prod/.env` is never overwritten by CI.

Desktop DMG / Windows Setup are built locally via `deploy/prod/package-desktop.sh` and copied into `deploy/prod/site/downloads/` on the host (not produced by Actions yet).
