-- Claim an idempotency key, or report what holds it. Runs atomically on the Redis primary.
-- KEYS[1] record hash   KEYS[2] fencing counter
-- ARGV[1] fingerprint   ARGV[2] owner token   ARGV[3] lease (ms)
-- Returns {1, fence} acquired | {2} in flight | {3, status, headers, body, replayable} completed | {4} mismatch
local state = redis.call('HGET', KEYS[1], 'state')
if not state then
  local fence = redis.call('INCR', KEYS[2])
  redis.call('HSET', KEYS[1], 'state', 'running', 'fp', ARGV[1], 'owner', ARGV[2], 'fence', fence)
  redis.call('PEXPIRE', KEYS[1], ARGV[3])
  return {1, fence}
end
if redis.call('HGET', KEYS[1], 'fp') ~= ARGV[1] then
  return {4}
end
if state == 'running' then
  return {2}
end
local r = redis.call('HMGET', KEYS[1], 'status', 'headers', 'body', 'replayable')
return {3, r[1], r[2], r[3], r[4]}
