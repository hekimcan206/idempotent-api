-- Extend a running claim held by the caller.
-- KEYS[1] record hash   ARGV[1] owner token   ARGV[2] lease (ms)
if redis.call('HGET', KEYS[1], 'state') ~= 'running' or redis.call('HGET', KEYS[1], 'owner') ~= ARGV[1] then
  return 0
end
redis.call('PEXPIRE', KEYS[1], ARGV[2])
return 1
