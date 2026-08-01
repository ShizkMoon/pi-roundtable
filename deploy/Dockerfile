FROM node:24-alpine AS build
WORKDIR /app

COPY package.json package-lock.json tsconfig.base.json ./
COPY packages ./packages
RUN npm ci
RUN npm run build
RUN npm prune --omit=dev

FROM node:24-alpine
WORKDIR /app
ENV NODE_ENV=production
ENV HOST=0.0.0.0
ENV PORT=4317

COPY --from=build /app/package.json ./package.json
COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/packages ./packages

USER node
EXPOSE 4317
CMD ["node", "packages/sync-server/dist/server.js"]
