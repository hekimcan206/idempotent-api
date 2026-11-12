-- Token bucket take. Same arithmetic as TokenBucketMath in C#; time comes from the Redis server clock so every
-- application instance sees one clock, whatever the skew between their hosts.
-- KEYS[1] bucket hash
-- ARGV[1] capacity   ARGV[2] refill per second   ARGV[3] cost
-- Returns {allowed (1/0), tokens (string), retry_after_us, reset_after_us}
local capacity = tonumber(ARGV[1])
local rate = tonumber(ARGV[2])
local cost = tonumber(ARGV[3])
local t = redis.call('TIME')
local now = tonumber(t[1]) * 1000000 + tonumber(t[2])

local b = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
local tokens = tonumber(b[1])
local ts = tonumber(b[2])
if tokens == nil or ts == nil then
  tokens = capacity
  ts = now
end
-- Never refill backwards (clock step after a failover): wait for time to catch up instead.
if now > ts then
  tokens = math.min(capacity, tokens + (now - ts) * rate / 1000000)
  ts = now
end

local allowed = 0
local retry_us = 0
if tokens >= cost - 1e-9 then
  tokens = math.max(0, tokens - cost)
  allowed = 1
else
  retry_us = math.ceil((cost - tokens) * 1000000 / rate)
end
local reset_us = math.ceil((capacity - tokens) * 1000000 / rate)

-- %.17g round-trips a double exactly; %.0f keeps a 16-digit microsecond timestamp intact (tostring would not).
redis.call('HSET', KEYS[1], 'tokens', string.format('%.17g', tokens), 'ts', string.format('%.0f', ts))
-- A bucket that has refilled completely carries no information, so it may expire then.
redis.call('PEXPIRE', KEYS[1], math.ceil(reset_us / 1000) + 1000)
return {allowed, string.format('%.17g', tokens), retry_us, reset_us}
