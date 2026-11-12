-- Store the final response, only if the caller still owns the running claim.
-- KEYS[1] record hash
-- ARGV[1] owner token  ARGV[2] status  ARGV[3] headers  ARGV[4] body  ARGV[5] replayable (1/0)  ARGV[6] retention (ms)
if redis.call('HGET', KEYS[1], 'state') ~= 'running' or redis.call('HGET', KEYS[1], 'owner') ~= ARGV[1] then
  return 0
end
redis.call('HSET', KEYS[1], 'state', 'done', 'status', ARGV[2], 'headers', ARGV[3], 'body', ARGV[4], 'replayable', ARGV[5])
redis.call('HDEL', KEYS[1], 'owner')
redis.call('PEXPIRE', KEYS[1], ARGV[6])
return 1
