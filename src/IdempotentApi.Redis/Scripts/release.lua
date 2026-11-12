-- Drop a running claim held by the caller so a retry can execute.
-- KEYS[1] record hash   ARGV[1] owner token
if redis.call('HGET', KEYS[1], 'state') ~= 'running' or redis.call('HGET', KEYS[1], 'owner') ~= ARGV[1] then
  return 0
end
redis.call('DEL', KEYS[1])
return 1
