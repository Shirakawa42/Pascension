// Exact byte-keyed, bounded LRU for frozen row-independent inference.
// Hashes locate entries; full row comparisons always verify identity.
#include <cstdint>
#include <cstring>
#include <list>
#include <unordered_map>
#include <vector>
extern "C" uint64_t XXH3_64bits(const void*, size_t);
struct Entry { uint64_t key; std::vector<float> row; float output[65]; };
struct Cache {
    size_t capacity, width; uint64_t mask;
    std::list<Entry> entries;
    std::unordered_map<uint64_t, std::list<Entry>::iterator> index;
    Cache(size_t c,size_t w,uint64_t m):capacity(c),width(w),mask(m){}
};
extern "C" {
void* cache_create(size_t capacity,size_t width,uint64_t mask) {
    try { return capacity&&width ? new Cache(capacity,width,mask) : nullptr; }
    catch(...) { return nullptr; }
}
void cache_destroy(void* handle) { delete static_cast<Cache*>(handle); }
int cache_lookup(void* handle,const float* packet,int count,float* output,int* sources,int* missing) {
    try {
        auto& c=*static_cast<Cache*>(handle);
        std::unordered_map<uint64_t,int> pending;
        int misses=0;size_t bytes=c.width*sizeof(float);
        for(int i=0;i<count;i++) {
            const float* row=packet+i*c.width;
            uint64_t key=XXH3_64bits(row,bytes)&c.mask;
            auto hit=c.index.find(key);auto same=pending.find(key);
            if(hit!=c.index.end() && !std::memcmp(hit->second->row.data(),row,bytes)) {
                std::memcpy(output+i*65,hit->second->output,65*sizeof(float));sources[i]=-1;
                c.entries.splice(c.entries.begin(),c.entries,hit->second);
            } else if(same!=pending.end() && !std::memcmp(packet+same->second*c.width,row,bytes)) {
                sources[i]=same->second;
            } else {
                pending[key]=i;sources[i]=i;missing[misses++]=i;
            }
        }
        return misses;
    } catch(...) { return -1; }
}
int cache_commit(void* handle,const float* packet,int count,float* output,const int* sources) {
    try {
        auto& c=*static_cast<Cache*>(handle);size_t bytes=c.width*sizeof(float);
        for(int i=0;i<count;i++) if(sources[i]>=0 && sources[i]!=i)
            std::memcpy(output+i*65,output+sources[i]*65,65*sizeof(float));
        for(int i=0;i<count;i++) if(sources[i]==i) {
            const float* row=packet+i*c.width;
            uint64_t key=XXH3_64bits(row,bytes)&c.mask;
            auto hit=c.index.find(key);
            if(hit!=c.index.end()) c.entries.splice(c.entries.begin(),c.entries,hit->second);
            else if(c.entries.size()==c.capacity) {
                auto last=std::prev(c.entries.end());c.index.erase(last->key);
                c.entries.splice(c.entries.begin(),c.entries,last);
            } else c.entries.push_front(Entry{0,std::vector<float>(c.width),{}});
            auto entry=c.entries.begin();entry->key=key;
            std::memcpy(entry->row.data(),row,bytes);
            std::memcpy(entry->output,output+i*65,65*sizeof(float));
            c.index[key]=entry;
        }
        return 0;
    } catch(...) { return -1; }
}
}
