from pathlib import Path
from functools import reduce
import os

class Nav_Generator:
    def generate_nav(self,directory):
        self._rename_section_index(list(Path(directory).rglob("*.[mM][dD]")))
        nav_tree = list(Path(directory).rglob("*.[mM][dD]"))
        nav_tree = map(lambda x: x.relative_to(directory),nav_tree)
        nav_tree = map(self._grow_branch,nav_tree)
        nav_tree = map(self._remove_prefixes,nav_tree)
        nav_tree = filter(self._is_not_draft,nav_tree)
        nav_tree = reduce(self._merge_branches, nav_tree, [])
        return nav_tree

    def _grow_branch(self,file):
        sub_paths=[]
        for parent in file.parents:
            if parent.name!="":
                sub_paths.append(parent.name)
        path_name = "/".join(reversed(sub_paths))
        branch = path_name+"/"+file.name
        for sub_path in sub_paths:
            branch = {sub_path:branch}
        return branch

    def _remove_prefixes(self, branch):
        if type(branch) == dict:
            for key, sub_branch in branch.items():
                try:
                    pre_post = key.split("_",1)
                    pre,post = pre_post[0],pre_post[1]
                    pre=int(pre)
                    key = post
                except (ValueError, IndexError, AttributeError):
                    pass
                branch = {key:self._remove_prefixes(sub_branch)}
        return branch

    def _merge_branches(self, branch1, branch2):
        if type(branch2) == str:
            return branch1
        b1_keys = self._get_keys(branch1)
        b2_top_key = self._get_first_key(branch2)
        if b2_top_key not in b1_keys:
            branch1.append(self._wrap_dicts_in_lists(branch2)) 
        else:
            b1_key_index = self._get_index_of_el(branch1,b2_top_key)
            sub_branch1=branch1[b1_key_index][b2_top_key]
            if type(sub_branch1) == str:
                branch1[b1_key_index].update({b2_top_key:[sub_branch1,branch2[b2_top_key]]})
            else:
                self._merge_branches(sub_branch1, branch2[b2_top_key])
        return branch1

    def _get_index_of_el(self,branch,key):
        keys = self._get_keys(branch)
        return keys.index(key)

    def _get_first_key(self,dictionary):
        return list(dictionary.keys())[0]

    def _get_keys(self,list_of_dicts): 
        list_of_list_of_keys = list(map(self._get_key_or_none,list_of_dicts))
        return reduce(lambda a, b : a + b, list_of_list_of_keys, [])

    def _get_key_or_none(self,dictionary):
        try:
            return list(dictionary.keys())
        except AttributeError:
            return []

    def _wrap_dicts_in_lists(self,input):
        key = self._get_first_key(input)
        val = input[key]
        if type(val)!= dict:
            return input
        return {key:[self._wrap_dicts_in_lists(val)]}

    def _is_not_draft(self, branch):
        if type(branch) == dict:
            for sub_branch in branch.values():
                return self._is_not_draft(sub_branch)
        else:
            file_name = Path(branch).name
            if file_name[0] == "_":
                return False
            else:
                return True

    def _rename_section_index(self,files):
        files = filter(self._is_section_index, files)
        files = filter(self._is_not_draft, files)
        for file in files:
            new_name = Path(file).parent / "index.md"
            if file != new_name:
                try:
                    os.remove(new_name)
                except FileNotFoundError:
                    pass
            os.rename(file, new_name)

    def _is_section_index(self, file):
        file_path = Path(file)
        file_extension = file_path.suffix.lower()
        if file_extension != ".md":
            return False
        parent = file_path.parent
        folders = filter(os.path.isdir,parent.glob("*"))
        folders = list(folders)
        if folders:
            return True
        else: 
            return False